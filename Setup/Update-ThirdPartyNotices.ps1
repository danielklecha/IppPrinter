<#
.SYNOPSIS
    Generates and updates THIRD-PARTY-NOTICES.md for a .NET project.

.DESCRIPTION
    Scans dependencies via project.assets.json, inspects NuGet packages (.nuspec,
    bundled license files, DLL metadata), inspects GitHub repositories for license/notice
    files, resolves SPDX standard licenses, aggregates embedded notices, and generates a
    clean, comprehensive THIRD-PARTY-NOTICES.md notice file with expandable full license texts.

.PARAMETER ProjectName
    Name of the project folder containing the .csproj. Defaults to "IppPrinter".

.PARAMETER OutputPath
    Optional custom destination path for the generated notice markdown file.
    Defaults to THIRD-PARTY-NOTICES.md in the repository root.

.PARAMETER SkipRestore
    If set, skips the initial 'dotnet restore' step.

.PARAMETER Verify
    If set (alias: -CheckOnly), runs in verification mode for CI/pre-commit:
    compares generated notices against the existing file on disk and exits with code 1
    if the file is missing or out of date, without modifying the file.

.PARAMETER FailOnWarning
    If set, exits with code 1 if any warnings were encountered during analysis
    (e.g., missing copyright, unresolved licenses, or stale overrides).

.PARAMETER Offline
    If set, disables all outbound network calls (to SPDX, GitHub, or project websites),
    relying exclusively on local packages and existing .license-cache data.

.EXAMPLE
    .\Update-ThirdPartyNotices.ps1

.EXAMPLE
    .\Update-ThirdPartyNotices.ps1 -ProjectName "IppPrinter" -Verbose

.EXAMPLE
    .\Update-ThirdPartyNotices.ps1 -Verify -FailOnWarning
#>
[CmdletBinding()]
param (
    [string]$ProjectName = "IppPrinter",
    [string]$OutputPath,
    [switch]$SkipRestore,
    [Alias("CheckOnly")]
    [switch]$Verify,
    [switch]$FailOnWarning,
    [switch]$Offline
)

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$repoRoot = Split-Path $scriptDir
$projectDir = Join-Path $repoRoot $ProjectName
$outputFile = if ($OutputPath) { [System.IO.Path]::GetFullPath($OutputPath) } else { Join-Path $repoRoot "THIRD-PARTY-NOTICES.md" }
$cacheDir = Join-Path $scriptDir ".license-cache"
$spdxCacheDir = Join-Path $cacheDir "spdx"
$githubCacheDir = Join-Path $cacheDir "github"
$webCacheDir = Join-Path $cacheDir "web"

# Cache stores (in-memory run-time caches to avoid redundant disk I/O and web requests)
$script:SpdxCache = @{}
$script:GitHubLicenseCache = @{}

# Cache hit & network request metrics for diagnostics and execution summary
$script:SpdxCacheHits = 0
$script:SpdxOnlineFetches = 0
$script:GitHubCacheHits = 0
$script:GitHubOnlineFetches = 0
$script:WebCacheHits = 0
$script:WebOnlineFetches = 0
$script:NetworkErrors = 0
$script:NetworkErrorDetails = [System.Collections.Generic.List[string]]::new()

# -----------------------------------------------------------------------------
# Override and Fallback Dictionaries (Kept as fallback/override circuit-breaker)
# -----------------------------------------------------------------------------
# These dictionaries act as an explicit override mechanism when automated resolution
# (nuspec, package directory, GitHub repository) fails or needs manual correction.
#
# Examples:
#   Licenses    = @{ "SomePackage" = "MIT" }
#   Copyrights  = @{ "PdfPig"      = "Copyright (c) 2018-2024 PdfPig Contributors" }
#   ProjectUrls = @{ "SomePackage" = "https://github.com/owner/repo" }
#   Notices     = @{ "SomePackage" = "Bundled libfoo: Copyright (c) 2020 Bar. MIT License." }
$PackageOverrides = @{
    # Known mappings for packages without explicit SPDX license declarations or standard license URLs
    Licenses    = @{}

    # Known copyright overrides when missing or incomplete in .nuspec and assembly metadata
    Copyrights  = @{}

    # Known project URLs when missing from .nuspec (neither projectUrl nor repository.url is declared)
    ProjectUrls = @{}

    # Extra bundled notices for packages that embed third-party assets without including notice files in nupkg
    Notices     = @{}
}

# -----------------------------------------------------------------------------
# 1. Helper & Network Functions
# -----------------------------------------------------------------------------

# Converts arbitrary text into a clean, URL-friendly anchor slug for markdown links
# e.g., "Microsoft .NET Library License Terms" -> "microsoft-net-library-license-terms"
function Get-Slug {
    param ([string]$text)
    if ([string]::IsNullOrWhiteSpace($text)) { return "" }
    $s = $text.ToLower() -replace '[^a-z0-9_-]', '-'
    $s = $s -replace '-+', '-'
    return $s.Trim('-')
}

# Safely extracts authentic copyright lines while filtering out:
# 1) Leading / trailing source comment markers (//, /*, *, #, <!--, rem, -->, */)
# 2) Standard disclaimer & liability boilerplate phrases that incidentally mention the word 'copyright'
#    (e.g., "Neither the name of the copyright holder nor...", "The above copyright notice must be retained",
#    "IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT...")
# 3) Unfilled license template placeholders (e.g., [yyyy], <year>, <copyright holders>, [name of copyright owner])
function Get-CopyrightLinesFromText {
    param ([string]$text)
    if ([string]::IsNullOrWhiteSpace($text)) { return @() }
    $lines = $text -split "`r?`n"
    $results = [System.Collections.Generic.List[string]]::new()
    foreach ($line in $lines) {
        $trimmed = $line.Trim()
        # Strip leading comment prefixes (e.g. //, /*, *, #, <!--, rem) and trailing markers (*/, -->)
        $trimmed = $trimmed -replace '^(?:/\*+|//|\*+|#|<!--|rem\b)\s*', ''
        $trimmed = $trimmed -replace '\s*(?:\*+/|-->)$', ''
        $trimmed = $trimmed.Trim()

        # Match line starting with standard copyright markers:
        # Exclude common disclaimer boilerplate lines that trigger false-positives by containing 'copyright':
        if ($trimmed -match "^(copyright\b|©|\(c\)|\(C\))" -and
            $trimmed -notmatch "(liable|disclaimed|warranties|merchantability|shall|in no event|to reproduce|must retain|included in or attached)" -and
            $trimmed -notmatch "(\[yyyy\]|<year>|\[name of|<copyright holders>)" -and
            $trimmed.Length -gt 5) {
            if (-not $results.Contains($trimmed)) {
                $results.Add($trimmed)
            }
        }
    }
    return $results.ToArray()
}

# Detects and separates embedded third-party notices or appendices from the primary license text.
# Handles:
# 1) Standard license termination clauses:
#    - Apache-2.0 / general: "END OF TERMS AND CONDITIONS", "END OF LICENSE TERMS", "END OF THE AGREEMENT"
#    - Component-level / bundled: "END OF MIT LICENSE", "END OF BSD LICENSE", "END OF NOTICES"
#    - Fenced/bracketed/commented variants: "--- END OF TERMS ---", "[END OF TERMS AND CONDITIONS]", "/* END OF ... */"
#    - License exhibits/appendices: MPL ("Exhibit A - Source Code Form License Notice"), CDDL ("EXHIBIT A"),
#      EPL ("EXHIBIT A – APPLICATION OF THIS LICENSE"), Apache ("APPENDIX: How to apply..."),
#      GNU GPL/LGPL ("How to Apply These Terms to Your New Programs / Libraries")
# 2) Distinguishing author/application instructions from downstream third-party notices appended after the appendix.
# 3) Prominent section dividers (--- / === / *** / ### / ___ / ~~~) followed by recognized third-party notice headers.
function Split-LicenseAndNotices {
    param ([string]$content)

    if ([string]::IsNullOrWhiteSpace($content)) {
        return @{ LicenseText = ""; NoticesText = "" }
    }

    # 1. Standard license termination clauses & Exhibit headers:
    # Matches:
    # - "END OF TERMS AND CONDITIONS", "END OF TERMS AND CONDITIONS FOR USE, REPRODUCTION, AND DISTRIBUTION",
    #   "END OF LICENSE TERMS", "END OF APACHE LICENSE", "END OF MIT LICENSE", "END OF BSD LICENSE", "END OF NOTICES"
    # - Exhibit/Appendix transitions: "EXHIBIT A - Source Code Form License Notice", "EXHIBIT A - APPLICATION OF THIS LICENSE", etc.
    # Group $Matches[1] captures the primary license text up to the termination marker.
    # Group $Matches[2] captures any remaining text (appendix, notices, or trailing comments).
    $endOfTermsKeyword = "(?:(?:/\*+|//+|#|\[)?\s*END OF (?:(?:THE\s+)?(?:APACHE|MIT|BSD|BSD\s+3-CLAUSE)?\s*(?:LICENSE|LICENCE|AGREEMENT|TERMS(?:\s+AND\s+CONDITIONS)?)|TERMS AND CONDITIONS(?:\s+FOR\s+USE,\s+REPRODUCTION,\s+AND\s+DISTRIBUTION)?|LICENSE TERMS(?:\s+AND\s+CONDITIONS)?|(?:THIRD[- ]PARTY\s+)?NOTICES)\s*(?:\*+/|\])?|[-=*]{3,}\s*END OF (?:TERMS(?: AND CONDITIONS)?|LICENSE(?: TERMS)?|LICENCE|MIT(?: LICENSE)?|BSD(?: LICENSE)?)\s*[-=*]{3,}|(?:[-=*]{3,}\s*)?EXHIBIT\s+[AB]\s*[-–—]\s*(?:Source Code Form License Notice|`"Incompatible With Secondary Licenses`" Notice|APPLICATION OF THIS LICENSE|Seven \(7\) Clauses|Initial Developer Notices|Mozilla Public License)|(?:[-=*]{3,}\s*)?APPENDIX:\s*(?:Application of this License|How to apply))"
    $endOfTermsPattern = "(?s)(.*?(?:(?<=\s|^)$endOfTermsKeyword))\s*(.*)"
    if ($content -match $endOfTermsPattern) {
        $primary = $Matches[1].Trim()
        $remainder = $Matches[2].Trim()

        # Check if remainder starts with an instructional appendix (Apache "How to apply...", GNU "How to Apply These Terms...", or MPL/CDDL/EPL exhibits).
        # - $Matches[1] captures the instructional body of the appendix.
        # - $Matches[2] captures any downstream third-party notices following a divider or notice header.
        # If no downstream third-party notices follow, the appendix is standard non-notice boilerplate and discarded from notices.
        $appendixPattern = "(?s)^(?:APPENDIX(?::\s*(?:How to apply the Apache License|Applying the Apache License|[^\r\n]*))?|Applying the Apache License[^\r\n]*|How to Apply These Terms[^\r\n]*|How to apply the GNU[^\r\n]*|EXHIBIT\s+[AB][^\r\n]*)\r?\n(.*?)(?:\r?\n\s*[-=*#_~]{5,}\s*\r?\n|\r?\n(?=(?:Third[- ]Party|Additional|Subcomponents|BUNDLED|This software|This product|%%|\*{5,}|#{5,})))?(.*)"
        if ($remainder -match $appendixPattern) {
            $appendixRest = $Matches[2].Trim()
            # Require downstream text to be substantial (>= 30 chars) to prevent returning empty/whitespace notice blocks
            if ($appendixRest.Length -gt 30) {
                return @{ LicenseText = $primary; NoticesText = $appendixRest }
            }
        }
        elseif ($remainder.Length -gt 30 -and $remainder -notmatch "^(?:APPENDIX|Applying the Apache License|How to Apply These Terms|How to apply the GNU|EXHIBIT\s+[AB])") {
            return @{ LicenseText = $primary; NoticesText = $remainder }
        }
    }

    # 2. General separator or header indicating third-party notices, subcomponent licenses, or attributions
    # Matches dividers formed by dashes, equals, asterisks, hashes, underscores, or tildes followed by recognizable notice headers.
    # Group $Matches[1] captures primary license text, $Matches[2] captures notice delimiter, $Matches[3] captures notice body.
    $delimiterKeywords = @(
        "Third[- ]Party (?:Notices?|Licenses?|Software|Attributions?|Code|Legal Notices?|Packages|Libraries|Artifacts|Components|Credits|Dependencies)",
        "Third[- ]Party (?:Software\s+)?(?:Attribution|Notices and Information)",
        "Additional (?:Licensing|Licenses?|Terms?|Notices?)",
        "Subcomponents(?::|\b)|Subcomponent Licenses",
        "BUNDLED (?:LIBRARIES|COMPONENTS|DEPENDENCIES|SOFTWARE)",
        "This software contains components licensed under",
        "This product (?:contains|includes) software developed (?:by|at)",
        "The following (?:third[- ]party|components|libraries|software)",
        "Licenses of Third[- ]Party Dependencies",
        "Licenses and notices for (?:third[- ]party|bundled)",
        "(?:Third[- ]Party\s+)?Acknowledgements",
        "(?:Third[- ]Party\s+)?Acknowledgments",
        "NOTICES AND INFORMATION",
        "Portions (?:copyright|derived from|created by|of this (?:software|work|code) are|of this software are based on)",
        "EXTERNAL (?:LIBRARIES|DEPENDENCIES|COMPONENTS)",
        "%%\s*(?:[A-Za-z0-9_.-]+\s+)?NOTICES",
        "Credits(?::|\b)|CREDITS",
        "Contains (?:software|code) from",
        "Used (?:Libraries|Components|Open Source)",
        "Vendored (?:Code|Libraries|Components)",
        "Attributions for (?:Third[- ]Party|external)",
        "Open Source (?:Components|Software|Licenses|Attributions)",
        "Dependency (?:Licenses?|License Information)",
        "LEGAL NOTICES"
    ) -join '|'

    $delimiterPattern = "(?s)(.*?)(?:\r?\n\s*[-=*#_~]{5,}\s*\r?\n|\r?\n)($delimiterKeywords)(.*)"
    if ($content -match $delimiterPattern) {
        $primary = $Matches[1].Trim()
        $delimiter = $Matches[2].Trim()
        $remainder = $Matches[3].Trim()
        # Require primary text to be substantial (>= 50 chars) and remainder to be non-trivial (>= 30 chars)
        if ($primary.Length -gt 50 -and $remainder.Length -gt 30) {
            $fullNotices = "$delimiter$remainder".Trim()
            return @{ LicenseText = $primary; NoticesText = $fullNotices }
        }
    }

    return @{ LicenseText = $content.Trim(); NoticesText = "" }
}

# Centralized detection of standard and common open-source licenses from file text content
# Note: Detection checks are intentionally ordered by specificity to prevent partial matches:
#   - MIT-0 before MIT (MIT-0 has no copyright notice attribution condition)
#   - AGPL before GPL (AGPL contains full General Public License name + network provision)
#   - LGPL before GPL (LGPL mentions General Public License)
#   - BSD-4 before BSD-3 before BSD-2 (BSD-4 adds advertising clause; BSD-3 adds non-endorsement clause)
#   - 0BSD before ISC (0BSD removes attribution clause from ISC template)
#   - CDDL-1.1 before CDDL-1.0 (CDDL-1.1 includes updated version notice)
#   - EPL-2.0 before EPL-1.0 (EPL-2.0 includes secondary license provision)
#   - OFL-1.1 before OFL-1.0 (OFL-1.1 includes reserved font name clarifications)
function Get-DetectedLicenseFromContent {
    param (
        [string]$content,
        [string]$packageId = ""
    )
    if ([string]::IsNullOrWhiteSpace($content)) { return "" }

    if ($content -match "MICROSOFT (?:PRE-RELEASE )?SOFTWARE LICENSE TERMS") {
        return "Microsoft .NET Library License Terms"
    }

    # MIT-0 check must precede standard MIT (MIT-0 has no copyright attribution requirement in conditions)
    if ($content -match "MIT No Attribution|\bMIT-0\b" -or
        ($content -match "Permission is hereby granted, free of charge" -and
         $content -match "without restriction" -and
         $content -match "THE SOFTWARE IS PROVIDED" -and
         $content -notmatch "The above copyright notice and this permission notice shall be included")) {
        return "MIT-0"
    }
    if ($content -match "Permission is hereby granted, free of charge" -and $content -match "without restriction") {
        return "MIT"
    }

    # Apache variants
    if ($content -match "(?i)Apache License[,\s]+Version 2\.0" -or
        $content -match "TERMS AND CONDITIONS FOR USE, REPRODUCTION, AND DISTRIBUTION" -or
        $content -match "https?://(?:www\.)?apache\.org/licenses/LICENSE-2\.0") {
        return "Apache-2.0"
    }
    if ($content -match "(?i)Apache Software License[,\s]+Version 1\.1") {
        return "Apache-1.1"
    }
    if ($content -match "(?i)Apache Software License[,\s]+Version 1\.0|Apache License[,\s]+Version 1\.0") {
        return "Apache-1.0"
    }

    # GNU Affero General Public License (AGPL checks must precede GPL checks since AGPL mentions GPL)
    if ($content -match "GNU Affero General Public License" -and ($content -match "Version 3" -or $content -match "\bAGPLv3\b")) {
        return "AGPL-3.0"
    }
    if ($content -match "GNU Affero General Public License" -and ($content -match "Version 1" -or $content -match "\bAGPLv1\b")) {
        return "AGPL-1.0"
    }

    # GNU Lesser General Public License (LGPL checks must precede GPL checks since LGPL mentions GPL)
    if ($content -match "GNU Lesser General Public License" -and ($content -match "Version 3" -or $content -match "\bLGPLv3\b")) {
        return "LGPL-3.0"
    }
    if ($content -match "GNU Lesser General Public License" -and ($content -match "Version 2\.1" -or $content -match "\bLGPLv2\.1\b")) {
        return "LGPL-2.1"
    }
    # GNU General Public License (GPL)
    if ($content -match "GNU General Public License" -and ($content -match "Version 3" -or $content -match "\bGPLv3\b")) {
        return "GPL-3.0"
    }
    if ($content -match "GNU General Public License" -and ($content -match "Version 2" -or $content -match "\bGPLv2\b")) {
        return "GPL-2.0"
    }

    # BSD-4-Clause check: contains advertising requirement clause 3
    if ($content -match "All advertising materials mentioning features or use of this software must display the following acknowledgement") {
        return "BSD-4-Clause"
    }
    # BSD-3-Clause-Clear check: contains the Clear BSD addition (grant of patent rights disclaimed)
    if ($content -match "Clear BSD License|BSD-3-Clause-Clear|patent license.*is granted.*under this license") {
        return "BSD-3-Clause-Clear"
    }
    # BSD-3-Clause check MUST precede BSD-2-Clause:
    # BSD-3 contains clause 2 text ("Redistributions in binary form..."), but adds clause 3:
    # "Neither the name of <organization/copyright holder> nor the names of its contributors..."
    # or the restriction on endorsement/promotion.
    if ($content -match "Neither the name of \S.+? nor the names of (?:its )?contributors" -or
        $content -match "endorse or promote products derived from this software without specific prior written permission") {
        return "BSD-3-Clause"
    }
    if ($content -match "Redistributions in binary form must reproduce the above copyright notice") {
        return "BSD-2-Clause"
    }

    # 0BSD check must precede ISC: 0BSD removes the copyright attribution requirement
    if ($content -match "Permission to use, copy, modify, and/(?:or )?distribute this software for any purpose with or without fee is hereby granted") {
        if ($content -match "THE AUTHOR DISCLAIMS ALL WARRANTIES" -and $content -notmatch "The above copyright notice and this permission notice") {
            return "0BSD"
        }
        return "ISC"
    }

    if ($content -match "Creative Commons Zero|CC0 1\.0 Universal|Public Domain Dedication") {
        return "CC0-1.0"
    }
    if ($content -match "Creative Commons Attribution 4\.0 International|\bCC-BY-4\.0\b") {
        return "CC-BY-4.0"
    }
    if ($content -match "Creative Commons Attribution-ShareAlike 4\.0|\bCC-BY-SA-4\.0\b") {
        return "CC-BY-SA-4.0"
    }
    if ($content -match "Creative Commons Attribution-ShareAlike 3\.0|\bCC-BY-SA-3\.0\b") {
        return "CC-BY-SA-3.0"
    }
    if ($content -match "Creative Commons Attribution-ShareAlike 2\.5|\bCC-BY-SA-2\.5\b") {
        return "CC-BY-SA-2.5"
    }
    if ($content -match "Creative Commons Attribution 3\.0|\bCC-BY-3\.0\b") {
        return "CC-BY-3.0"
    }
    if ($content -match "Creative Commons Attribution 2\.5|\bCC-BY-2\.5\b") {
        return "CC-BY-2.5"
    }
    # Creative Commons NonCommercial / NoDerivatives variants (restrictive — not permissive open-source licenses)
    if ($content -match "Creative Commons Attribution-NonCommercial 4\.0|\bCC-BY-NC-4\.0\b") {
        return "CC-BY-NC-4.0"
    }
    if ($content -match "Creative Commons Attribution-NonCommercial 3\.0|\bCC-BY-NC-3\.0\b") {
        return "CC-BY-NC-3.0"
    }
    if ($content -match "Creative Commons Attribution-NoDerivatives 4\.0|\bCC-BY-ND-4\.0\b") {
        return "CC-BY-ND-4.0"
    }
    if ($content -match "Creative Commons Attribution-NoDerivatives 3\.0|\bCC-BY-ND-3\.0\b") {
        return "CC-BY-ND-3.0"
    }
    if ($content -match "This is free and unencumbered software released into the public domain") {
        return "Unlicense"
    }
    if ($content -match "DO WHAT THE FUCK YOU WANT TO PUBLIC LICENSE|\bWTFPL\b") {
        return "WTFPL"
    }
    if ($content -match "Microsoft Public License|\bMS-PL\b") {
        return "MS-PL"
    }
    if ($content -match "Microsoft Reciprocal License|\bMS-RL\b") {
        return "MS-RL"
    }
    if ($content -match "Microsoft Limited Public License|\bMS-LPL\b") {
        return "MS-LPL"
    }
    if ($content -match "Boost Software License - Version 1\.0") {
        return "BSL-1.0"
    }
    if ($content -match "Eclipse Public License (?:- v 2\.0|Version 2\.0)") {
        return "EPL-2.0"
    }
    if ($content -match "Eclipse Public License (?:- v 1\.0|Version 1\.0)") {
        return "EPL-1.0"
    }
    if ($content -match "Mozilla Public License,?[ \t]+v\.?[ \t]*2\.0") {
        return "MPL-2.0"
    }
    if ($content -match "Mozilla Public License,?[ \t]+v\.?[ \t]*1\.1") {
        return "MPL-1.1"
    }
    if ($content -match "zlib/libpng License|Permission is granted to anyone to use this software for any purpose,\s*including commercial applications") {
        return "Zlib"
    }
    if ($content -match "PostgreSQL (?:License|Licence)" -or
        ($content -match "Permission to use, copy, modify, and distribute this software and its documentation for any purpose" -and
         $content -match "PostgreSQL Global Development Group")) {
        return "PostgreSQL"
    }
    if ($content -match "SIL OPEN FONT LICENSE Version 1\.1|SIL Open Font License") {
        return "OFL-1.1"
    }
    if ($content -match "SIL OPEN FONT LICENSE Version 1\.0") {
        return "OFL-1.0"
    }
    if ($content -match "UNICODE, INC\. LICENSE AGREEMENT - DATA FILES AND SOFTWARE|Unicode Data Files and Software") {
        return "Unicode-DFS-2016"
    }
    if ($content -match "COMMON DEVELOPMENT AND DISTRIBUTION LICENSE \(CDDL\) Version 1\.1") {
        return "CDDL-1.1"
    }
    if ($content -match "COMMON DEVELOPMENT AND DISTRIBUTION LICENSE \(CDDL\) Version 1\.0|\bCDDL-1\.0\b") {
        return "CDDL-1.0"
    }
    if ($content -match "OpenSSL License" -and $content -match "Original SSLeay License") {
        return "OpenSSL"
    }
    if ($content -match "COPYRIGHT AND PERMISSION NOTICE" -and ($content -match "curl and libcurl" -or $content -match "Daniel Stenberg")) {
        return "curl"
    }
    if ($content -match "ICU License" -or ($content -match "Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files \(the `"Software`"\)" -and $content -match "Unicode, Inc\. and others")) {
        return "ICU"
    }
    if ($content -match "bzip2 and libbzip2 License" -or ($content -match "Julian R Seward" -and $content -match "Redistribution and use in source and binary forms")) {
        return "bzip2-1.0.6"
    }
    if ($content -match "The Software shall be used for Good, not Evil") {
        return "JSON"
    }
    if ($content -match "European Union Public Licence[,\s]+(?:version\s+)?1\.2|\bEUPL-1\.2\b") {
        return "EUPL-1.2"
    }
    if ($content -match "European Union Public Licence[,\s]+(?:version\s+)?1\.1|\bEUPL-1\.1\b") {
        return "EUPL-1.1"
    }
    if ($content -match "Server Side Public License|\bSSPL-1\.0\b") {
        return "SSPL-1.0"
    }
    if ($content -match "Universal Permissive License|\bUPL-1\.0\b") {
        return "UPL-1.0"
    }
    if ($content -match "The Artistic License 2\.0|\bArtistic-2\.0\b") {
        return "Artistic-2.0"
    }
    if ($content -match "The Code Project Open License|\bCPOL-1\.02\b") {
        return "CPOL-1.02"
    }
    if ($content -match "Academic Free License[,\s]+(?:v\.|version)?\s*3\.0|\bAFL-3\.0\b") {
        return "AFL-3.0"
    }
    if ($content -match "Academic Free License[,\s]+(?:v\.|version)?\s*2\.1|\bAFL-2\.1\b") {
        return "AFL-2.1"
    }
    if ($content -match "University of Illinois/NCSA Open Source License|\bNCSA\b") {
        return "NCSA"
    }
    if ($content -match "Blue Oak Model License|\bBlueOak-1\.0\.0\b") {
        return "BlueOak-1.0.0"
    }
    if ($content -match "W3C SOFTWARE NOTICE AND LICENSE|W3C\s+(?:Software|Document)\s+(?:License|Notice)") {
        return "W3C"
    }
    # HPND (Historical Permission Notice and Disclaimer): permission-based license using "without fee"
    # instead of MIT's "free of charge", ending with "without express or implied warranty"
    if ($content -match "without fee is hereby granted" -and
        $content -match "without express or implied warranty") {
        return "HPND"
    }
    if ($content -match "SAX.*(?:public domain|Public Domain)|\bSAX-PD\b") {
        return "SAX-PD"
    }
    if ($content -match "PNG Reference Library License|\bLibpng-2\.0\b") {
        return "Libpng-2.0"
    }

    if (-not [string]::IsNullOrWhiteSpace($packageId)) {
        return "$packageId License Terms"
    }

    return ""
}

# Fetch standard license text from SPDX online repository with caching
function Get-StandardLicenseText {
    param (
        [string]$licenseId,
        [string]$cacheDirectory,
        [switch]$offline
    )
    if ([string]::IsNullOrWhiteSpace($licenseId)) { return "" }

    # 1. In-memory cache
    if ($script:SpdxCache.ContainsKey($licenseId)) {
        $script:SpdxCacheHits++
        Write-Verbose "    [CACHE-MEM] In-memory cache hit for standard license '$licenseId'"
        return $script:SpdxCache[$licenseId]
    }

    # 2. Disk cache
    $diskCacheFile = Join-Path $cacheDirectory "$licenseId.txt"
    if (Test-Path $diskCacheFile) {
        $cachedText = Get-Content $diskCacheFile -Raw -Encoding UTF8
        if (-not [string]::IsNullOrWhiteSpace($cachedText)) {
            $script:SpdxCacheHits++
            Write-Verbose "    [CACHE-DISK] Disk cache hit for standard license '$licenseId'"
            $script:SpdxCache[$licenseId] = $cachedText
            return $cachedText
        }
    }

    # 3. Online fetch from SPDX official sources
    if ($offline) {
        Write-Verbose "    [OFFLINE] Skipping SPDX online fetch for '$licenseId'"
        return "Please refer to https://spdx.org/licenses/$licenseId.html for full license terms."
    }

    $candidateUris = @(
        "https://raw.githubusercontent.com/spdx/license-list-data/main/text/$licenseId.txt",
        "https://spdx.org/licenses/$licenseId.txt"
    )

    foreach ($uri in $candidateUris) {
        try {
            $script:SpdxOnlineFetches++
            $text = (Invoke-RestMethod -Uri $uri -TimeoutSec 10 -ErrorAction Stop)
            if (-not [string]::IsNullOrWhiteSpace($text)) {
                $script:SpdxCache[$licenseId] = $text
                Set-Content -Path $diskCacheFile -Value $text -Encoding utf8
                Write-Host "    [FETCH] Retrieved standard license text for '$licenseId' from $uri" -ForegroundColor DarkCyan
                return $text
            }
        }
        catch {
            $script:NetworkErrors++
            $script:NetworkErrorDetails.Add("SPDX standard license '$licenseId' ($uri): $($_.Exception.Message)")
            Write-Verbose "    [FETCH-FAIL] Could not retrieve license text for '$licenseId' from $($uri) - $_"
        }
    }

    Write-Warning "Could not fetch online license text for ${licenseId}."
    return "Please refer to https://spdx.org/licenses/$licenseId.html for full license terms."
}

# Extract GitHub owner and repo from repository/project/license URLs, or by inspecting project web pages
function Get-GitHubRepoInfo {
    param (
        [string]$url,
        [string]$cacheDirectory,
        [switch]$offline
    )
    if ([string]::IsNullOrWhiteSpace($url)) { return $null }

    # 1. Matches github.com/:owner/:repo or git@github.com::owner/:repo
    if ($url -match "github\.com[/:](?<owner>[^/]+)/(?<repo>[^/#?]+)") {
        $owner = $Matches["owner"]
        $repo = $Matches["repo"]
        if ($repo.EndsWith(".git")) { $repo = $repo.Substring(0, $repo.Length - 4) }
        return @{ Owner = $owner; Repo = $repo; FromPage = $false }
    }

    # 2. If URL is a project website/landing page, fetch page HTML and search for github.com links
    if ($url -match "^https?://" -and $url -notmatch "(microsoft\.com|dot\.net|apache\.org|nuget\.org|spdx\.org)") {
        $cleanKey = [System.Text.RegularExpressions.Regex]::Replace($url, "[^a-zA-Z0-9_.-]", "_")
        if ($cleanKey.Length -gt 100) { $cleanKey = $cleanKey.Substring(0, 100) }
        $pageCacheFile = Join-Path $cacheDirectory "$cleanKey.json"

        if (Test-Path $pageCacheFile) {
            try {
                $cached = Get-Content $pageCacheFile -Raw -Encoding UTF8 | ConvertFrom-Json
                if ($cached.Owner) {
                    $script:WebCacheHits++
                    Write-Verbose "    [CACHE-PAGE] Found GitHub repo info for $url in cache ($($cached.Owner)/$($cached.Repo))"
                    return @{ Owner = $cached.Owner; Repo = $cached.Repo; FromPage = $true }
                }
                return $null
            }
            catch {
                Write-Verbose "    [CACHE-PAGE-ERR] Failed parsing disk cache $pageCacheFile - $_"
            }
        }

        if ($offline) {
            Write-Verbose "    [OFFLINE] Skipping web inspection for $url"
            return $null
        }

        try {
            $script:WebOnlineFetches++
            $html = (Invoke-RestMethod -Uri $url -TimeoutSec 5 -Headers @{ "User-Agent" = "PowerShell" } -ErrorAction Stop)
            if ($html -is [string] -and $html -match "https?://github\.com/(?<owner>[a-zA-Z0-9_.-]+)/(?<repo>[a-zA-Z0-9_.-]+)") {
                $owner = $Matches["owner"]
                $repo = $Matches["repo"].TrimEnd('/', '"', "'", '.', '>')
                if ($repo.EndsWith(".git")) { $repo = $repo.Substring(0, $repo.Length - 4) }
                # Filter out generic GitHub navigational or corporate pages
                $ignored = @("features", "pricing", "about", "contact", "site", "topics", "collections", "events", "readme", "spdx", "github")
                if ($ignored -notcontains $owner.ToLower()) {
                    $found = [PSCustomObject]@{ Owner = $owner; Repo = $repo }
                    $found | ConvertTo-Json | Set-Content -Path $pageCacheFile -Encoding utf8
                    Write-Verbose "    [DISCOVER] Found GitHub repo from landing page $url -> $owner/$repo"
                    return @{ Owner = $owner; Repo = $repo; FromPage = $true }
                }
            }
            # Cache negative result (page inspected cleanly, no github repo found)
            [PSCustomObject]@{ Owner = $null; Repo = $null } | ConvertTo-Json | Set-Content -Path $pageCacheFile -Encoding utf8
        }
        catch {
            $script:NetworkErrors++
            $status = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
            if ($status -eq 404) {
                # Only cache negative result on disk if server answered 404 (avoids caching transient connection drops or 5xx)
                [PSCustomObject]@{ Owner = $null; Repo = $null } | ConvertTo-Json | Set-Content -Path $pageCacheFile -Encoding utf8
            }
            elseif ($status -in 403, 429) {
                Write-Warning "    [RATE-LIMIT] HTTP rate limit or access restriction (HTTP $status) encountered when inspecting $url"
                $script:NetworkErrorDetails.Add("Landing page rate limit '$url': HTTP $status")
            }
            else {
                $script:NetworkErrorDetails.Add("Landing page inspection '$url': $($_.Exception.Message)")
            }
            Write-Verbose "    [DISCOVER-FAIL] Could not inspect project landing page $url - $_"
        }
    }

    return $null
}

# Fetch license and notice files directly from GitHub repository with caching
function Get-GitHubLicenseInfo {
    param (
        [string]$owner,
        [string]$repo,
        [string]$preferredFile = "",
        [string]$cacheDirectory,
        [switch]$offline
    )

    $cacheKey = "$owner/$repo".ToLower()
    if ($script:GitHubLicenseCache.ContainsKey($cacheKey)) {
        $script:GitHubCacheHits++
        return $script:GitHubLicenseCache[$cacheKey]
    }

    $diskCacheFile = Join-Path $cacheDirectory "$($owner)_$($repo).json"
    if (Test-Path $diskCacheFile) {
        try {
            $json = Get-Content $diskCacheFile -Raw -Encoding UTF8 | ConvertFrom-Json
            $script:GitHubCacheHits++
            Write-Verbose "    [CACHE-GH] Disk cache hit for GitHub license $owner/$repo"
            $script:GitHubLicenseCache[$cacheKey] = $json
            return $json
        }
        catch {
            Write-Verbose "    [CACHE-GH-ERR] Failed parsing disk cache $diskCacheFile - $_"
        }
    }

    $result = [PSCustomObject]@{
        LicenseFound    = $false
        LicenseText     = ""
        DetectedLicense = ""
        Copyrights      = @()
        NoticesText     = ""
        SourceFile      = ""
        SourceUrl       = ""
    }

    if ($offline) {
        Write-Verbose "    [OFFLINE] Skipping GitHub fetch for $owner/$repo"
        return $result
    }

    $sawHttpNotFound = $false

    # Candidate filenames for primary license
    $candidateFiles = [System.Collections.Generic.List[string]]::new()
    if (-not [string]::IsNullOrWhiteSpace($preferredFile)) {
        $candidateFiles.Add($preferredFile)
    }
    foreach ($cand in @("LICENSE", "LICENSE.txt", "LICENSE.md", "license.txt", "license.md", "COPYING", "Copyright")) {
        if (-not $candidateFiles.Contains($cand)) {
            $candidateFiles.Add($cand)
        }
    }

    # Try fetching license file from raw.githubusercontent.com HEAD branch
    foreach ($cand in $candidateFiles) {
        $rawUrl = "https://raw.githubusercontent.com/$owner/$repo/HEAD/$cand"
        try {
            $script:GitHubOnlineFetches++
            $content = (Invoke-RestMethod -Uri $rawUrl -TimeoutSec 5 -ErrorAction Stop)
            if (-not [string]::IsNullOrWhiteSpace($content) -and $content.Length -gt 20) {
                $result.LicenseFound = $true
                $result.LicenseText = $content.Trim()
                $result.SourceFile = $cand
                $result.SourceUrl = $rawUrl
                break
            }
        }
        catch {
            $script:NetworkErrors++
            $status = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
            if ($status -eq 404) { $sawHttpNotFound = $true }
            if ($status -in 403, 429) {
                Write-Warning "    [GH-RATE-LIMIT] GitHub rate limit or access restriction reached (HTTP $status) for $rawUrl"
                $script:NetworkErrorDetails.Add("GitHub raw rate limit '$rawUrl': HTTP $status")
            }
            elseif ($status -ne 404) {
                $script:NetworkErrorDetails.Add("GitHub fetch '$rawUrl': $($_.Exception.Message)")
            }
            Write-Verbose "    [GH-FETCH] Failed fetching $cand from $rawUrl - $_"
        }
    }

    # Candidate filenames for notice file
    $noticeFiles = @("NOTICES.txt", "NOTICES", "NOTICE.txt", "NOTICE", "ThirdPartyNotices.txt")
    foreach ($nCand in $noticeFiles) {
        $nRawUrl = "https://raw.githubusercontent.com/$owner/$repo/HEAD/$nCand"
        try {
            $script:GitHubOnlineFetches++
            $nContent = (Invoke-RestMethod -Uri $nRawUrl -TimeoutSec 5 -ErrorAction Stop)
            if (-not [string]::IsNullOrWhiteSpace($nContent) -and $nContent.Length -gt 20) {
                $result.NoticesText = $nContent.Trim()
                break
            }
        }
        catch {
            $script:NetworkErrors++
            $status = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
            if ($status -in 403, 429) {
                Write-Warning "    [GH-RATE-LIMIT] GitHub rate limit or access restriction reached (HTTP $status) for $nRawUrl"
                $script:NetworkErrorDetails.Add("GitHub notice rate limit '$nRawUrl': HTTP $status")
            }
            elseif ($status -ne 404) {
                $script:NetworkErrorDetails.Add("GitHub notice fetch '$nRawUrl': $($_.Exception.Message)")
            }
            Write-Verbose "    [GH-NOTICE-FETCH] Failed fetching notice $nCand from $nRawUrl - $_"
        }
    }

    if ($result.LicenseFound) {
        # Check if the license file embeds external component notices or appendix
        $split = Split-LicenseAndNotices -content $result.LicenseText
        if (-not [string]::IsNullOrWhiteSpace($split.NoticesText)) {
            if ([string]::IsNullOrWhiteSpace($result.NoticesText)) {
                $result.NoticesText = $split.NoticesText
            }
            $result.LicenseText = $split.LicenseText
        }
        $content = $result.LicenseText

        # Extract copyright lines from license file
        $result.Copyrights = Get-CopyrightLinesFromText -text $content

        # Detect standard license type from file content
        $result.DetectedLicense = Get-DetectedLicenseFromContent -content $content
    }

    # Save to disk cache only if found or definitively answered with 404 (avoids caching transient timeouts or network drops)
    if ($result.LicenseFound -or $sawHttpNotFound) {
        $result | ConvertTo-Json -Depth 5 | Set-Content -Path $diskCacheFile -Encoding utf8
    }
    $script:GitHubLicenseCache[$cacheKey] = $result
    return $result
}

# -----------------------------------------------------------------------------
# 2. Package Inspection & Resolution Functions
# -----------------------------------------------------------------------------

# Reads and parses metadata from the package's .nuspec file
function Get-NuspecMetadata {
    param (
        [string]$packageDir,
        [string]$packageId
    )

    $nuspecPath = Join-Path $packageDir "$($packageId.ToLower()).nuspec"
    if (-not (Test-Path $nuspecPath)) {
        $nuspecFiles = Get-ChildItem -Path $packageDir -File -Filter "*.nuspec"
        if ($nuspecFiles.Count -gt 0) {
            $nuspecPath = $nuspecFiles[0].FullName
            Write-Verbose "    [NUSPEC-FALLBACK] Expected nuspec '$($packageId.ToLower()).nuspec' not found; using '$($nuspecFiles[0].Name)'"
        }
    }

    $result = [PSCustomObject]@{
        HasNuspec     = $false
        Authors       = ""
        Copyright     = ""
        LicenseType   = ""
        LicenseValue  = ""
        LicenseUrl    = ""
        ProjectUrl    = ""
        RepositoryUrl = ""
    }

    if (Test-Path $nuspecPath) {
        $result.HasNuspec = $true
        [xml]$xml = Get-Content $nuspecPath -Raw -Encoding UTF8
        $metadata = $xml.package.metadata
        if ($metadata) {
            if ($metadata.authors) { $result.Authors = $metadata.authors.Trim() }
            if ($metadata.copyright) { $result.Copyright = $metadata.copyright.Trim() }
            if ($metadata.license) {
                $result.LicenseType = $metadata.license.type
                $result.LicenseValue = $metadata.license.InnerText.Trim()
            }
            if ($metadata.licenseUrl) { $result.LicenseUrl = $metadata.licenseUrl.Trim() }
            if ($metadata.projectUrl) { $result.ProjectUrl = $metadata.projectUrl.Trim() }
            if ($metadata.repository -and $metadata.repository.url) {
                $result.RepositoryUrl = $metadata.repository.url.Trim()
            }
        }
    }

    return $result
}

# Resolves the project URL from nuspec, repository URL, or overrides
function Resolve-PackageProjectUrl {
    param (
        [string]$packageId,
        [PSCustomObject]$nuspec,
        [hashtable]$overrides
    )

    if (-not [string]::IsNullOrWhiteSpace($nuspec.ProjectUrl)) {
        return @{ Url = $nuspec.ProjectUrl; Source = "nuspec <projectUrl>" }
    }
    if (-not [string]::IsNullOrWhiteSpace($nuspec.RepositoryUrl)) {
        $url = $nuspec.RepositoryUrl
        if ($url -match "^https?://.*\.git$") {
            $url = $url.Substring(0, $url.Length - 4)
        }
        return @{ Url = $url; Source = "nuspec <repository.url>" }
    }
    if ($overrides.ProjectUrls.ContainsKey($packageId)) {
        return @{ Url = $overrides.ProjectUrls[$packageId]; Source = "overrides.ProjectUrls" }
    }

    return @{ Url = ""; Source = "" }
}

# Inspects package directory for bundled license files, DLL FileVersionInfo, and detects custom license text
function Get-PackageFileInfo {
    param (
        [string]$packageDir,
        [string]$packageId,
        [PSCustomObject]$nuspec
    )

    $assemblyCopyright = ""
    $dllFiles = @(Get-ChildItem -Path $packageDir -Recurse -File -Filter "*.dll")
    if ($dllFiles.Count -gt 0) {
        try {
            # Prefer primary assembly matching package ID over satellite resources or native helper binaries
            $targetDll = $dllFiles | Where-Object { $_.BaseName -ieq $packageId } | Select-Object -First 1
            if (-not $targetDll) { $targetDll = $dllFiles[0] }
            $verInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($targetDll.FullName)
            if (-not [string]::IsNullOrWhiteSpace($verInfo.LegalCopyright)) {
                $assemblyCopyright = $verInfo.LegalCopyright.Trim()
            }
        }
        catch {
            Write-Verbose "    [DLL-INSPECT-FAIL] Failed extracting assembly copyright for package $packageId - $_"
        }
    }

    $licenseFiles = @()
    if ($nuspec.LicenseType -eq "file" -and -not [string]::IsNullOrWhiteSpace($nuspec.LicenseValue)) {
        $declaredLicensePath = Join-Path $packageDir $nuspec.LicenseValue
        if (Test-Path $declaredLicensePath) {
            $licenseFiles = @(Get-Item $declaredLicensePath)
        }
    }
    if ($licenseFiles.Count -eq 0) {
        $licenseFiles = @(Get-ChildItem -Path $packageDir -Recurse -File | Where-Object {
                $_.Name -match "^(license|licence|copying|dotnet_library_license)" -and
                $_.Name -notmatch "(third-party|thirdparty|notices)"
            })
    }

    $noticeFiles = @(Get-ChildItem -Path $packageDir -Recurse -File | Where-Object {
            $_.Name -match "(third[-_]?party|notices?|attribution)" -and
            $_.Name -notmatch "^(license|licence|copying|dotnet_library_license)"
        })

    $extractedCopyrights = [System.Collections.Generic.List[string]]::new()
    $customLicenseText = ""
    $fileDetectedLicense = ""

    if ($licenseFiles.Count -gt 0) {
        Write-Verbose "    [PKG-LICENSE] Selected bundled license file: $($licenseFiles[0].FullName)"
        $fileContent = Get-Content $licenseFiles[0].FullName -Raw -Encoding UTF8

        # Check if the bundled license file embeds subcomponent notices or appendix
        $split = Split-LicenseAndNotices -content $fileContent
        if (-not [string]::IsNullOrWhiteSpace($split.NoticesText)) {
            $noticeFiles = @($noticeFiles) + [PSCustomObject]@{
                FullName = $licenseFiles[0].FullName
                Name     = "$($licenseFiles[0].Name) (embedded notices)"
                Content  = $split.NoticesText
            }
            $fileContent = $split.LicenseText
        }

        foreach ($c in (Get-CopyrightLinesFromText -text $fileContent)) {
            $extractedCopyrights.Add($c)
        }

        $detected = Get-DetectedLicenseFromContent -content $fileContent -packageId $packageId
        $fileDetectedLicense = $detected

        if ($detected -eq "Microsoft .NET Library License Terms" -or $detected -eq "$packageId License Terms") {
            $customLicenseText = $fileContent.Trim()
        }
    }

    return [PSCustomObject]@{
        AssemblyCopyright    = $assemblyCopyright
        ExtractedCopyrights  = $extractedCopyrights
        CustomLicenseText    = $customLicenseText
        FileDetectedLicense  = $fileDetectedLicense
        NoticeFiles          = $noticeFiles
    }
}

# Resolves copyright statement through multi-source waterfall:
# Precedence:
# 1) Explicit user overrides in $PackageOverrides.Copyrights
# 2) Bundled package license files (.nupkg)
# 3) GitHub repository license file (HEAD branch)
# 4) .nuspec <copyright> (appends <authors> if only year/year-range is present)
# 5) Primary assembly FileVersionInfo.LegalCopyright
# 6) .nuspec <authors> fallback
# 7) Unresolved placeholder ("No copyright notice provided.")
function Resolve-PackageCopyright {
    param (
        [string]$packageId,
        [PSCustomObject]$nuspec,
        [PSCustomObject]$fileInspection,
        [object]$gitHubInfo,
        [object]$ghRepo,
        [hashtable]$overrides,
        [System.Collections.Generic.List[string]]$notes,
        [System.Collections.Generic.List[string]]$warnings
    )

    if ($overrides.Copyrights.ContainsKey($packageId)) {
        $notes.Add("Applied copyright override from configuration")
        return @{ Copyright = $overrides.Copyrights[$packageId]; Source = "knownCopyrightOverrides" }
    }
    if ($fileInspection.ExtractedCopyrights.Count -gt 0) {
        return @{ Copyright = ($fileInspection.ExtractedCopyrights -join "`n"); Source = "bundled package license file" }
    }
    if ($gitHubInfo -and $gitHubInfo.Copyrights -and $gitHubInfo.Copyrights.Count -gt 0) {
        return @{ Copyright = ($gitHubInfo.Copyrights -join "`n"); Source = "GitHub license file ($($ghRepo.Owner)/$($ghRepo.Repo))" }
    }
    if (-not [string]::IsNullOrWhiteSpace($nuspec.Copyright)) {
        $c = $nuspec.Copyright
        if ($c -match "^\s*(copyright\b|©|\(c\)|\(C\))\s+\d{4}(?:\s*[-–—]\s*\d{4})?$") {
            $final = if (-not [string]::IsNullOrWhiteSpace($nuspec.Authors)) { "$c $($nuspec.Authors)" } else { $c }
            $notes.Add("nuspec copyright only contained year or year range; appended authors")
            return @{ Copyright = $final; Source = "nuspec <copyright> + <authors>" }
        }
        if ($c -match "^\s*(copyright\b|©|\(c\)|\(C\))") {
            return @{ Copyright = $c; Source = "nuspec <copyright>" }
        }
        return @{ Copyright = "Copyright (c) $c"; Source = "nuspec <copyright>" }
    }
    if (-not [string]::IsNullOrWhiteSpace($fileInspection.AssemblyCopyright)) {
        $ac = $fileInspection.AssemblyCopyright
        $final = if ($ac -match "^\s*(copyright\b|©|\(c\)|\(C\))") { $ac } else { "Copyright (c) $ac" }
        return @{ Copyright = $final; Source = "assembly FileVersionInfo" }
    }
    if (-not [string]::IsNullOrWhiteSpace($nuspec.Authors)) {
        $notes.Add("Copyright missing in nuspec/assembly; synthesized from <authors>")
        return @{ Copyright = "Copyright (c) $($nuspec.Authors)"; Source = "nuspec <authors> (fallback)" }
    }

    $warnings.Add("No copyright information found anywhere for package")
    return @{ Copyright = "No copyright notice provided."; Source = "none" }
}

# Resolves primary license and custom license text through multi-source waterfall:
# Precedence:
# 1) .nuspec <license type="expression"> (explicit SPDX expression)
# 2) Explicit user overrides in $PackageOverrides.Licenses
# 3) Bundled package license file content detection (Get-DetectedLicenseFromContent)
# 4) GitHub repository license file content detection
# 5) .nuspec <licenseUrl> heuristics (pattern-matching against known URLs)
# 6) Custom / Unrecognized fallback ("Custom / See notices" + custom license text)
function Resolve-PackageLicense {
    param (
        [string]$packageId,
        [PSCustomObject]$nuspec,
        [PSCustomObject]$fileInspection,
        [object]$gitHubInfo,
        [object]$ghRepo,
        [hashtable]$overrides,
        [System.Collections.Generic.List[string]]$warnings
    )

    $primaryLicense = ""
    $licenseSource = ""
    $customText = $fileInspection.CustomLicenseText
    $isDeclared = $false

    if ($nuspec.LicenseType -eq "expression") {
        $primaryLicense = $nuspec.LicenseValue
        $licenseSource = "nuspec <license type=`"expression`">"
        $isDeclared = $true
    }
    elseif ($overrides.Licenses.ContainsKey($packageId)) {
        $primaryLicense = $overrides.Licenses[$packageId]
        $licenseSource = "knownLicenseMappings"
    }
    elseif (-not [string]::IsNullOrWhiteSpace($fileInspection.FileDetectedLicense)) {
        $primaryLicense = $fileInspection.FileDetectedLicense
        $licenseSource = "bundled package license file"
    }
    elseif ($gitHubInfo -and -not [string]::IsNullOrWhiteSpace($gitHubInfo.DetectedLicense)) {
        $primaryLicense = $gitHubInfo.DetectedLicense
        $licenseSource = "GitHub license detection ($($ghRepo.Owner)/$($ghRepo.Repo))"
    }
    # MIT-0 must precede MIT (MIT-0 URLs also match the broader "MIT" pattern)
    elseif ($nuspec.LicenseUrl -match "MIT-0|mit-0") {
        $primaryLicense = "MIT-0"
        $licenseSource = "nuspec <licenseUrl> heuristic"
    }
    elseif ($nuspec.LicenseUrl -match "MIT") {
        $primaryLicense = "MIT"
        $licenseSource = "nuspec <licenseUrl> heuristic"
    }
    # Apache-1.x must precede generic Apache (which defaults to 2.0)
    elseif ($nuspec.LicenseUrl -match "apache[-_]?1\.1") {
        $primaryLicense = "Apache-1.1"
        $licenseSource = "nuspec <licenseUrl> heuristic"
    }
    elseif ($nuspec.LicenseUrl -match "apache[-_]?1\.0") {
        $primaryLicense = "Apache-1.0"
        $licenseSource = "nuspec <licenseUrl> heuristic"
    }
    elseif ($nuspec.LicenseUrl -match "apache") {
        $primaryLicense = "Apache-2.0"
        $licenseSource = "nuspec <licenseUrl> heuristic"
    }
    # BSD variants: 0BSD and Clear BSD must precede numbered BSD checks; BSD-4 before BSD-3 before generic BSD
    elseif ($nuspec.LicenseUrl -match "0bsd") {
        $primaryLicense = "0BSD"
        $licenseSource = "nuspec <licenseUrl> heuristic"
    }
    elseif ($nuspec.LicenseUrl -match "clear-bsd|bsd-3-clause-clear") {
        $primaryLicense = "BSD-3-Clause-Clear"
        $licenseSource = "nuspec <licenseUrl> heuristic"
    }
    elseif ($nuspec.LicenseUrl -match "bsd[-_]?4") {
        $primaryLicense = "BSD-4-Clause"
        $licenseSource = "nuspec <licenseUrl> heuristic"
    }
    elseif ($nuspec.LicenseUrl -match "bsd[-_]?3") {
        $primaryLicense = "BSD-3-Clause"
        $licenseSource = "nuspec <licenseUrl> heuristic"
    }
    elseif ($nuspec.LicenseUrl -match "bsd") {
        $primaryLicense = "BSD-2-Clause"
        $licenseSource = "nuspec <licenseUrl> heuristic"
    }
    elseif ($nuspec.LicenseUrl -match "isc") {
        $primaryLicense = "ISC"
        $licenseSource = "nuspec <licenseUrl> heuristic"
    }
    elseif ($nuspec.LicenseUrl -match "gnu\.org/licenses/agpl-3|agpl-3|agplv3") {
        $primaryLicense = "AGPL-3.0"
        $licenseSource = "nuspec <licenseUrl> heuristic"
    }
    elseif ($nuspec.LicenseUrl -match "gnu\.org/licenses/agpl-1|agpl-1|agplv1") {
        $primaryLicense = "AGPL-1.0"
        $licenseSource = "nuspec <licenseUrl> heuristic"
    }
    elseif ($nuspec.LicenseUrl -match "gnu\.org/licenses/lgpl-3|lgpl-3|lgplv3") {
        $primaryLicense = "LGPL-3.0"
        $licenseSource = "nuspec <licenseUrl> heuristic"
    }
    elseif ($nuspec.LicenseUrl -match "gnu\.org/licenses/lgpl-2\.1|lgpl-2\.1|lgplv2\.1") {
        $primaryLicense = "LGPL-2.1"
        $licenseSource = "nuspec <licenseUrl> heuristic"
    }
    elseif ($nuspec.LicenseUrl -match "gnu\.org/licenses/gpl-3|gpl-3|gplv3") {
        $primaryLicense = "GPL-3.0"
        $licenseSource = "nuspec <licenseUrl> heuristic"
    }
    elseif ($nuspec.LicenseUrl -match "gnu\.org/licenses/gpl-2|gpl-2|gplv2") {
        $primaryLicense = "GPL-2.0"
        $licenseSource = "nuspec <licenseUrl> heuristic"
    }
    elseif ($nuspec.LicenseUrl -match "mozilla\.org/MPL/2\.0|/mpl-2\.0|\bmpl\b") {
        $primaryLicense = "MPL-2.0"
        $licenseSource = "nuspec <licenseUrl> heuristic"
    }
    elseif ($nuspec.LicenseUrl -match "mozilla\.org/MPL/1\.1|/mpl-1\.1") {
        $primaryLicense = "MPL-1.1"
        $licenseSource = "nuspec <licenseUrl> heuristic"
    }
    elseif ($nuspec.LicenseUrl -match "eclipse\.org/legal/epl-2\.0|/epl-2\.0") {
        $primaryLicense = "EPL-2.0"
        $licenseSource = "nuspec <licenseUrl> heuristic"
    }
    elseif ($nuspec.LicenseUrl -match "eclipse\.org/legal/epl-v10|/epl-1\.0") {
        $primaryLicense = "EPL-1.0"
        $licenseSource = "nuspec <licenseUrl> heuristic"
    }
    elseif ($nuspec.LicenseUrl -match "boost\.org/LICENSE_1_0|\bboost\b") {
        $primaryLicense = "BSL-1.0"
        $licenseSource = "nuspec <licenseUrl> heuristic"
    }
    elseif ($nuspec.LicenseUrl -match "\bzlib\b") {
        $primaryLicense = "Zlib"
        $licenseSource = "nuspec <licenseUrl> heuristic"
    }
    elseif ($nuspec.LicenseUrl -match "postgresql") {
        $primaryLicense = "PostgreSQL"
        $licenseSource = "nuspec <licenseUrl> heuristic"
    }
    elseif ($nuspec.LicenseUrl -match "scripts\.sil\.org/OFL|/ofl-1\.1|\bofl\b") {
        $primaryLicense = "OFL-1.1"
        $licenseSource = "nuspec <licenseUrl> heuristic"
    }
    elseif ($nuspec.LicenseUrl -match "ofl-1\.0") {
        $primaryLicense = "OFL-1.0"
        $licenseSource = "nuspec <licenseUrl> heuristic"
    }
    elseif ($nuspec.LicenseUrl -match "ms-pl|mspl") {
        $primaryLicense = "MS-PL"
        $licenseSource = "nuspec <licenseUrl> heuristic"
    }
    elseif ($nuspec.LicenseUrl -match "ms-rl|msrl") {
        $primaryLicense = "MS-RL"
        $licenseSource = "nuspec <licenseUrl> heuristic"
    }
    elseif ($nuspec.LicenseUrl -match "ms-lpl") {
        $primaryLicense = "MS-LPL"
        $licenseSource = "nuspec <licenseUrl> heuristic"
    }
    elseif ($nuspec.LicenseUrl -match "cc0") {
        $primaryLicense = "CC0-1.0"
        $licenseSource = "nuspec <licenseUrl> heuristic"
    }
    elseif ($nuspec.LicenseUrl -match "creativecommons\.org/licenses/by/4\.0|cc-by-4") {
        $primaryLicense = "CC-BY-4.0"
        $licenseSource = "nuspec <licenseUrl> heuristic"
    }
    elseif ($nuspec.LicenseUrl -match "creativecommons\.org/licenses/by-sa/4\.0|cc-by-sa-4") {
        $primaryLicense = "CC-BY-SA-4.0"
        $licenseSource = "nuspec <licenseUrl> heuristic"
    }
    elseif ($nuspec.LicenseUrl -match "creativecommons\.org/licenses/by/3\.0|cc-by-3") {
        $primaryLicense = "CC-BY-3.0"
        $licenseSource = "nuspec <licenseUrl> heuristic"
    }
    elseif ($nuspec.LicenseUrl -match "wtfpl") {
        $primaryLicense = "WTFPL"
        $licenseSource = "nuspec <licenseUrl> heuristic"
    }
    elseif ($nuspec.LicenseUrl -match "openssl") {
        $primaryLicense = "OpenSSL"
        $licenseSource = "nuspec <licenseUrl> heuristic"
    }
    elseif ($nuspec.LicenseUrl -match "curl\.se/docs/copyright|curl") {
        $primaryLicense = "curl"
        $licenseSource = "nuspec <licenseUrl> heuristic"
    }
    elseif ($nuspec.LicenseUrl -match "icu-project\.org|/icu") {
        $primaryLicense = "ICU"
        $licenseSource = "nuspec <licenseUrl> heuristic"
    }
    elseif ($nuspec.LicenseUrl -match "bzip2") {
        $primaryLicense = "bzip2-1.0.6"
        $licenseSource = "nuspec <licenseUrl> heuristic"
    }
    elseif ($nuspec.LicenseUrl -match "eupl-1\.2|/eupl\b") {
        $primaryLicense = "EUPL-1.2"
        $licenseSource = "nuspec <licenseUrl> heuristic"
    }
    elseif ($nuspec.LicenseUrl -match "eupl-1\.1") {
        $primaryLicense = "EUPL-1.1"
        $licenseSource = "nuspec <licenseUrl> heuristic"
    }
    elseif ($nuspec.LicenseUrl -match "sspl") {
        $primaryLicense = "SSPL-1.0"
        $licenseSource = "nuspec <licenseUrl> heuristic"
    }
    elseif ($nuspec.LicenseUrl -match "\bupl\b|upl-1\.0") {
        $primaryLicense = "UPL-1.0"
        $licenseSource = "nuspec <licenseUrl> heuristic"
    }
    elseif ($nuspec.LicenseUrl -match "artistic-2") {
        $primaryLicense = "Artistic-2.0"
        $licenseSource = "nuspec <licenseUrl> heuristic"
    }
    elseif ($nuspec.LicenseUrl -match "cpol") {
        $primaryLicense = "CPOL-1.02"
        $licenseSource = "nuspec <licenseUrl> heuristic"
    }
    elseif ($nuspec.LicenseUrl -match "afl-3") {
        $primaryLicense = "AFL-3.0"
        $licenseSource = "nuspec <licenseUrl> heuristic"
    }
    elseif ($nuspec.LicenseUrl -match "afl-2") {
        $primaryLicense = "AFL-2.1"
        $licenseSource = "nuspec <licenseUrl> heuristic"
    }
    elseif ($nuspec.LicenseUrl -match "ncsa") {
        $primaryLicense = "NCSA"
        $licenseSource = "nuspec <licenseUrl> heuristic"
    }
    elseif ($nuspec.LicenseUrl -match "unlicense") {
        $primaryLicense = "Unlicense"
        $licenseSource = "nuspec <licenseUrl> heuristic"
    }
    elseif ($nuspec.LicenseUrl -match "cddl-1\.1") {
        $primaryLicense = "CDDL-1.1"
        $licenseSource = "nuspec <licenseUrl> heuristic"
    }
    elseif ($nuspec.LicenseUrl -match "cddl") {
        $primaryLicense = "CDDL-1.0"
        $licenseSource = "nuspec <licenseUrl> heuristic"
    }
    elseif ($nuspec.LicenseUrl -match "blueoak|blue-oak") {
        $primaryLicense = "BlueOak-1.0.0"
        $licenseSource = "nuspec <licenseUrl> heuristic"
    }
    elseif ($nuspec.LicenseUrl -match "cc-by-nc-4|by-nc/4\.0") {
        $primaryLicense = "CC-BY-NC-4.0"
        $licenseSource = "nuspec <licenseUrl> heuristic"
    }
    elseif ($nuspec.LicenseUrl -match "cc-by-nc-3|by-nc/3\.0") {
        $primaryLicense = "CC-BY-NC-3.0"
        $licenseSource = "nuspec <licenseUrl> heuristic"
    }
    elseif ($nuspec.LicenseUrl -match "cc-by-nd-4|by-nd/4\.0") {
        $primaryLicense = "CC-BY-ND-4.0"
        $licenseSource = "nuspec <licenseUrl> heuristic"
    }
    elseif ($nuspec.LicenseUrl -match "cc-by-nd-3|by-nd/3\.0") {
        $primaryLicense = "CC-BY-ND-3.0"
        $licenseSource = "nuspec <licenseUrl> heuristic"
    }
    elseif ($nuspec.LicenseUrl -match "libpng|Libpng") {
        $primaryLicense = "Libpng-2.0"
        $licenseSource = "nuspec <licenseUrl> heuristic"
    }
    elseif ($nuspec.LicenseUrl -match "\bw3c\b|/W3C") {
        $primaryLicense = "W3C"
        $licenseSource = "nuspec <licenseUrl> heuristic"
    }
    elseif ($nuspec.LicenseUrl -match "\bhpnd\b|historical-permission") {
        $primaryLicense = "HPND"
        $licenseSource = "nuspec <licenseUrl> heuristic"
    }
    elseif ($nuspec.LicenseUrl -match "sax-pd|sax.*public.domain") {
        $primaryLicense = "SAX-PD"
        $licenseSource = "nuspec <licenseUrl> heuristic"
    }
    else {
        $primaryLicense = "Custom / See notices"
        $licenseSource = "custom/unrecognized"
        $warnings.Add("Unrecognized or missing license declaration")
    }

    # Embed custom license text if needed
    if ([string]::IsNullOrWhiteSpace($customText) -and $gitHubInfo -and $gitHubInfo.LicenseFound) {
        if ($primaryLicense -eq "Custom / See notices" -or $primaryLicense -match "License Terms$") {
            $customText = $gitHubInfo.LicenseText
        }
    }

    return [PSCustomObject]@{
        PrimaryLicense    = $primaryLicense
        LicenseSource     = $licenseSource
        CustomLicenseText = $customText
        IsDeclared        = $isDeclared
    }
}

# Collects and deduplicates notice files from package, GitHub, and overrides
function Resolve-PackageNotices {
    param (
        [string]$packageId,
        [PSCustomObject]$fileInspection,
        [object]$gitHubInfo,
        [object]$ghRepo,
        [hashtable]$overrides
    )

    $packageNotices = [System.Collections.Generic.List[string]]::new()
    $noticeSources = [System.Collections.Generic.List[string]]::new()
    $seenNoticeHashes = [System.Collections.Generic.HashSet[string]]::new()

    foreach ($nFile in $fileInspection.NoticeFiles) {
        $nText = if ($nFile.Content) { $nFile.Content.Trim() } else { (Get-Content $nFile.FullName -Raw -Encoding UTF8).Trim() }
        if (-not [string]::IsNullOrWhiteSpace($nText)) {
            $nHash = [System.Security.Cryptography.SHA256]::Create().ComputeHash(
                [System.Text.Encoding]::UTF8.GetBytes($nText)
            )
            $nHashStr = [BitConverter]::ToString($nHash)
            if (-not $seenNoticeHashes.Contains($nHashStr)) {
                $null = $seenNoticeHashes.Add($nHashStr)
                $packageNotices.Add($nText)
                $noticeSources.Add("bundled package file '$($nFile.Name)'")
            }
        }
    }

    if ($overrides.Notices.ContainsKey($packageId)) {
        $packageNotices.Add($overrides.Notices[$packageId].Trim())
        $noticeSources.Add("knownExtraNotices configuration")
    }
    elseif ($gitHubInfo -and -not [string]::IsNullOrWhiteSpace($gitHubInfo.NoticesText)) {
        $packageNotices.Add($gitHubInfo.NoticesText.Trim())
        $noticeSources.Add("GitHub repository notices ($($ghRepo.Owner)/$($ghRepo.Repo))")
    }

    $fullNoticesText = ($packageNotices -join "`n`n-------------------------------------------------------------------------------`n`n").Trim()

    return [PSCustomObject]@{
        PackageNotices  = $packageNotices
        NoticeSources   = $noticeSources
        FullNoticesText = $fullNoticesText
    }
}

# Writes clean structured tree diagnostics to console
function Write-PackageStatusLog {
    param (
        [string]$packageId,
        [string]$version,
        [string]$depType,
        [string]$primaryLicense,
        [string]$licenseSource,
        [string]$finalCopyright,
        [string]$copyrightSource,
        [string]$projectUrl,
        [string]$projectUrlSource,
        [System.Collections.Generic.List[string]]$packageNotices,
        [System.Collections.Generic.List[string]]$noticeSources,
        [System.Collections.Generic.List[string]]$notes,
        [System.Collections.Generic.List[string]]$warnings,
        [System.Collections.Generic.List[string]]$diagWarningsList
    )

    $statusTag = "[OK]  "
    $statusColor = "Green"
    if ($warnings.Count -gt 0) {
        $statusTag = "[WARN]"
        $statusColor = "Yellow"
        foreach ($w in $warnings) {
            $diagWarningsList.Add("$packageId ($version): $w")
        }
    }
    elseif ($notes.Count -gt 0) {
        $statusTag = "[INFO]"
        $statusColor = "Cyan"
    }

    Write-Host "$statusTag $packageId ($version) [$depType]" -ForegroundColor $statusColor
    Write-Host "  ├─ License:   $primaryLicense [$licenseSource]"

    $copyrightLines = $finalCopyright -split "`r?`n"
    if ($copyrightLines.Count -le 1) {
        Write-Host "  ├─ Copyright: $finalCopyright [$copyrightSource]"
    }
    else {
        Write-Host "  ├─ Copyright: $($copyrightLines[0])"
        for ($i = 1; $i -lt $copyrightLines.Count; $i++) {
            $suffix = if ($i -eq $copyrightLines.Count - 1) { " [$copyrightSource]" } else { "" }
            Write-Host "  │             $($copyrightLines[$i])$suffix"
        }
    }
    if (-not [string]::IsNullOrWhiteSpace($projectUrl)) {
        Write-Host "  ├─ Project:   $projectUrl [$projectUrlSource]"
    }
    else {
        Write-Host "  ├─ Project:   [NOT SPECIFIED]" -ForegroundColor Yellow
    }
    if ($packageNotices.Count -gt 0) {
        $nSrc = $noticeSources -join ", "
        Write-Host "  └─ Notices:   Includes $($packageNotices.Count) notice block(s) [$nSrc]"
    }
    else {
        Write-Host "  └─ Notices:   None required"
    }

    foreach ($note in $notes) {
        Write-Host "      ↳ Note: $note" -ForegroundColor DarkGray
    }
    foreach ($warn in $warnings) {
        Write-Host "      ↳ Warning: $warn" -ForegroundColor Yellow
    }
}

# -----------------------------------------------------------------------------
# 3. Document Generation Functions
# -----------------------------------------------------------------------------

# Helper to format license markdown link, supporting exact matches, custom licenses, and compound SPDX expressions (e.g., "MIT OR Apache-2.0")
function Get-LicenseMarkdownLink {
    param (
        [string]$license,
        [System.Collections.Generic.HashSet[string]]$allStandardLicenseIds,
        [System.Collections.Generic.List[PSObject]]$customLicenseGroups
    )
    if ([string]::IsNullOrWhiteSpace($license)) { return "Unspecified" }

    # 1. Exact match for custom license groups
    foreach ($cGroup in $customLicenseGroups) {
        if ($license -eq $cGroup.LicenseName) {
            $slug = Get-Slug $cGroup.LicenseName
            return "[$license](#license-$slug)"
        }
    }

    # 2. Exact match for a standard license
    if ($allStandardLicenseIds.Contains($license)) {
        $slug = Get-Slug $license
        return "[$license](#license-$slug)"
    }

    # 3. Compound SPDX expressions or text containing standard licenses (e.g., "MIT OR Apache-2.0")
    # We sort standard license IDs by descending string length so longer IDs are replaced before
    # their shorter prefixes (e.g., replacing "BSD-3-Clause" before "BSD" to avoid partial string substitution).
    $linkedLicense = $license
    $hasReplaced = $false
    foreach ($stdId in ($allStandardLicenseIds | Sort-Object Length -Descending)) {
        $pattern = "(^|[^a-zA-Z0-9_-])($([System.Text.RegularExpressions.Regex]::Escape($stdId)))([^a-zA-Z0-9_-]|$)"
        if ($linkedLicense -match $pattern) {
            $slug = Get-Slug $stdId
            $linkedLicense = [System.Text.RegularExpressions.Regex]::Replace($linkedLicense, $pattern, "`${1}[$stdId](#license-$slug)`${3}")
            $hasReplaced = $true
        }
    }
    if ($hasReplaced) {
        return $linkedLicense
    }

    return $license
}

# Generates the full THIRD-PARTY-NOTICES.md markdown text
function Build-ThirdPartyNoticesMarkdown {
    param (
        [System.Collections.Generic.List[PSObject]]$packageEntries,
        [System.Collections.Generic.List[PSObject]]$noticesGroups,
        [System.Collections.Generic.List[PSObject]]$customLicenseGroups,
        [System.Collections.Generic.HashSet[string]]$allStandardLicenseIds,
        [string]$spdxCacheDir,
        [switch]$offline
    )

    $sb = [System.Text.StringBuilder]::new()

    $sb.AppendLine("# Third-Party Software Notices and Information") | Out-Null
    $sb.AppendLine() | Out-Null
    $sb.AppendLine("This project incorporates third-party components and source code under various open-source licenses.") | Out-Null
    $sb.AppendLine("The original copyright notices, license terms, and embedded third-party notices are reproduced below.") | Out-Null
    $sb.AppendLine() | Out-Null
    $sb.AppendLine("## Table of Contents") | Out-Null
    $sb.AppendLine() | Out-Null
    $sb.AppendLine("- [1. Third-Party Dependency Inventory & Attributions](#1-third-party-dependency-inventory--attributions)") | Out-Null
    $sb.AppendLine("  - [1.1 Direct Dependencies](#11-direct-dependencies)") | Out-Null
    $sb.AppendLine("  - [1.2 Transitive Dependencies](#12-transitive-dependencies)") | Out-Null
    $sb.AppendLine("- [2. Common Open Source License Texts](#2-common-open-source-license-texts)") | Out-Null
    if ($noticesGroups.Count -gt 0) {
        $sb.AppendLine("- [3. Embedded & Subcomponent Notices](#3-embedded--subcomponent-notices)") | Out-Null
    }
    $sb.AppendLine() | Out-Null

    # Section 1.1 Direct Dependencies
    $sb.AppendLine("## 1. Third-Party Dependency Inventory & Attributions") | Out-Null
    $sb.AppendLine() | Out-Null
    $sb.AppendLine("### 1.1 Direct Dependencies") | Out-Null
    $sb.AppendLine() | Out-Null
    $sb.AppendLine("Packages directly referenced by the project:") | Out-Null
    $sb.AppendLine() | Out-Null
    $sb.AppendLine("| Package | Version | License | Copyright / Attribution | Project / Repository |") | Out-Null
    $sb.AppendLine("| :--- | :--- | :--- | :--- | :--- |") | Out-Null

    $directPackages = $packageEntries | Where-Object { $_.IsDirect } | Sort-Object Id
    foreach ($entry in $directPackages) {
        $projLink = if (-not [string]::IsNullOrWhiteSpace($entry.ProjectUrl)) { "[Project Link]($($entry.ProjectUrl))" } else { "-" }
        $licLink = Get-LicenseMarkdownLink -license $entry.License -allStandardLicenseIds $allStandardLicenseIds -customLicenseGroups $customLicenseGroups

        $matchedNoticeGroup = $null
        if (-not [string]::IsNullOrWhiteSpace($entry.ThirdPartyNotices)) {
            $matchedNoticeGroup = $noticesGroups | Where-Object { $_.NoticeText -eq $entry.ThirdPartyNotices } | Select-Object -First 1
        }
        $licDisplay = if ($matchedNoticeGroup) { "$licLink<br>([Notices](#$($matchedNoticeGroup.AnchorId)))" } else { $licLink }
        # Sanitize copyright lines for Markdown table cells:
        # Literal '|' is escaped to avoid breaking column structure, HTML entities are escaped,
        # and multiple lines are joined with '<br>' because raw newlines break GFM table rows.
        $cDisplay = (($entry.Copyright -split "`r?`n") | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object {
            $_.Trim().Replace('&', '&amp;').Replace('<', '&lt;').Replace('>', '&gt;').Replace('|', '\|')
        }) -join '<br>'

        $sb.AppendLine("| ``$($entry.Id)`` | ``$($entry.Version)`` | $licDisplay | $cDisplay | $projLink |") | Out-Null
    }
    $sb.AppendLine() | Out-Null

    # Section 1.2 Transitive Dependencies
    $sb.AppendLine("### 1.2 Transitive Dependencies") | Out-Null
    $sb.AppendLine() | Out-Null
    $sb.AppendLine("Secondary packages resolved transitively through dependencies:") | Out-Null
    $sb.AppendLine() | Out-Null
    $sb.AppendLine("| Package | Version | License | Copyright / Attribution | Project / Repository |") | Out-Null
    $sb.AppendLine("| :--- | :--- | :--- | :--- | :--- |") | Out-Null

    $transitivePackages = $packageEntries | Where-Object { -not $_.IsDirect } | Sort-Object Id
    foreach ($entry in $transitivePackages) {
        $projLink = if (-not [string]::IsNullOrWhiteSpace($entry.ProjectUrl)) { "[Project Link]($($entry.ProjectUrl))" } else { "-" }
        $licLink = Get-LicenseMarkdownLink -license $entry.License -allStandardLicenseIds $allStandardLicenseIds -customLicenseGroups $customLicenseGroups

        $matchedNoticeGroup = $null
        if (-not [string]::IsNullOrWhiteSpace($entry.ThirdPartyNotices)) {
            $matchedNoticeGroup = $noticesGroups | Where-Object { $_.NoticeText -eq $entry.ThirdPartyNotices } | Select-Object -First 1
        }
        $licDisplay = if ($matchedNoticeGroup) { "$licLink<br>([Notices](#$($matchedNoticeGroup.AnchorId)))" } else { $licLink }
        # Sanitize copyright lines for Markdown table cells:
        # Literal '|' is escaped to avoid breaking column structure, HTML entities are escaped,
        # and multiple lines are joined with '<br>' because raw newlines break GFM table rows.
        $cDisplay = (($entry.Copyright -split "`r?`n") | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object {
            $_.Trim().Replace('&', '&amp;').Replace('<', '&lt;').Replace('>', '&gt;').Replace('|', '\|')
        }) -join '<br>'

        $sb.AppendLine("| ``$($entry.Id)`` | ``$($entry.Version)`` | $licDisplay | $cDisplay | $projLink |") | Out-Null
    }
    $sb.AppendLine() | Out-Null

    # Section 2: Standard & Custom Licenses
    $sb.AppendLine("## 2. Common Open Source License Texts") | Out-Null
    $sb.AppendLine() | Out-Null
    $sb.AppendLine("The following standard open source licenses and custom license agreements govern the components listed in this project:") | Out-Null
    $sb.AppendLine() | Out-Null

    Write-Host "`nResolving standard license texts (SPDX online + cache)..." -ForegroundColor Cyan

    $preferredOrder = @("MIT", "BSD-2-Clause", "BSD-3-Clause", "Apache-2.0")
    $sortedStandardLicenses = [System.Collections.Generic.List[string]]::new()
    foreach ($pref in $preferredOrder) {
        if ($allStandardLicenseIds.Contains($pref)) {
            $sortedStandardLicenses.Add($pref)
        }
    }
    foreach ($stdId in ($allStandardLicenseIds | Sort-Object)) {
        if (-not $sortedStandardLicenses.Contains($stdId)) {
            $sortedStandardLicenses.Add($stdId)
        }
    }

    foreach ($stdId in $sortedStandardLicenses) {
        $licenseBody = Get-StandardLicenseText -licenseId $stdId -cacheDirectory $spdxCacheDir -offline:$offline
        $licenseSlug = Get-Slug $stdId
        $sb.AppendLine("<a id=""license-$licenseSlug""></a>") | Out-Null
        $sb.AppendLine("### $stdId License") | Out-Null
        $sb.AppendLine() | Out-Null
        $sb.AppendLine("<details>") | Out-Null
        $sb.AppendLine("<summary><strong>Click to expand $stdId License text</strong></summary>") | Out-Null
        $sb.AppendLine() | Out-Null
        $sb.AppendLine('```text') | Out-Null
        $sb.AppendLine($licenseBody.Trim()) | Out-Null
        $sb.AppendLine('```') | Out-Null
        $sb.AppendLine() | Out-Null
        $sb.AppendLine("</details>") | Out-Null
        $sb.AppendLine() | Out-Null
    }

    foreach ($cGroup in $customLicenseGroups) {
        $cSlug = Get-Slug $cGroup.LicenseName
        $sb.AppendLine("<a id=""license-$cSlug""></a>") | Out-Null
        $sb.AppendLine("### $($cGroup.LicenseName)") | Out-Null
        $pkgList = ($cGroup.Packages | ForEach-Object { "$($_.Id) ($($_.Version))" }) -join ", "
        $sb.AppendLine() | Out-Null
        $sb.AppendLine("*Applies to: $pkgList*") | Out-Null
        $sb.AppendLine() | Out-Null
        $sb.AppendLine("<details>") | Out-Null
        $sb.AppendLine("<summary><strong>Click to expand $($cGroup.LicenseName) text</strong></summary>") | Out-Null
        $sb.AppendLine() | Out-Null
        $sb.AppendLine('```text') | Out-Null
        $sb.AppendLine($cGroup.LicenseText.Trim()) | Out-Null
        $sb.AppendLine('```') | Out-Null
        $sb.AppendLine() | Out-Null
        $sb.AppendLine("</details>") | Out-Null
        $sb.AppendLine() | Out-Null
    }

    # Section 3: Subcomponent Notices
    if ($noticesGroups.Count -gt 0) {
        $sb.AppendLine("## 3. Embedded & Subcomponent Notices") | Out-Null
        $sb.AppendLine() | Out-Null
        $sb.AppendLine("The following notices cover third-party components, bundled vendor code, or native binaries distributed within the packages indicated below:") | Out-Null
        $sb.AppendLine() | Out-Null

        foreach ($group in $noticesGroups) {
            $sb.AppendLine("<a id=""$($group.AnchorId)""></a>") | Out-Null
            $pkgList = ($group.Packages | ForEach-Object { "$($_.Id) ($($_.Version))" }) -join ", "
            $sb.AppendLine("### Notices for $pkgList") | Out-Null
            $sb.AppendLine() | Out-Null
            $sb.AppendLine("<details>") | Out-Null
            $sb.AppendLine("<summary><strong>Click to expand full notice text</strong></summary>") | Out-Null
            $sb.AppendLine() | Out-Null
            $sb.AppendLine('```text') | Out-Null
            $sb.AppendLine($group.NoticeText.Trim()) | Out-Null
            $sb.AppendLine('```') | Out-Null
            $sb.AppendLine() | Out-Null
            $sb.AppendLine("</details>") | Out-Null
            $sb.AppendLine() | Out-Null
        }
    }

    return $sb.ToString()
}

# -----------------------------------------------------------------------------
# 4. Main Execution Flow
# -----------------------------------------------------------------------------

# Remove legacy .txt notices file if present
$oldTextFile = Join-Path $repoRoot "THIRD-PARTY-NOTICES.txt"
if (Test-Path $oldTextFile) {
    Remove-Item -Path $oldTextFile -Force -ErrorAction SilentlyContinue
}

# Ensure local cache directories exist
$null = New-Item -ItemType Directory -Path $spdxCacheDir -Force -ErrorAction SilentlyContinue
$null = New-Item -ItemType Directory -Path $githubCacheDir -Force -ErrorAction SilentlyContinue
$null = New-Item -ItemType Directory -Path $webCacheDir -Force -ErrorAction SilentlyContinue

$scriptTimer = [System.Diagnostics.Stopwatch]::StartNew()
Write-Host "Updating THIRD-PARTY-NOTICES.md: $outputFile (project: $ProjectName)..." -ForegroundColor Cyan
if ($Offline) {
    Write-Host "[OFFLINE] Running in offline mode: network calls disabled; using local packages and cache." -ForegroundColor Yellow
}
Write-Verbose "Using license cache directory: $cacheDir"

# Restore project to ensure project.assets.json is generated/updated
if (-not $SkipRestore -and -not $Offline) {
    Write-Host "Restoring NuGet packages to generate/update project.assets.json..."
    $projectCsproj = Join-Path $projectDir "$ProjectName.csproj"
    dotnet restore $projectCsproj
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "dotnet restore failed with exit code $LASTEXITCODE; proceeding with potentially stale project.assets.json"
    }
}

# Find project.assets.json
$assetsFile = Join-Path $projectDir "obj\project.assets.json"
if (-not (Test-Path $assetsFile)) {
    Write-Error "project.assets.json not found at $assetsFile. Please build or restore the project first."
    exit 1
}

# Parse project.assets.json
$assets = Get-Content $assetsFile -Raw -Encoding UTF8 | ConvertFrom-Json
$libraries = $assets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq "package" }

# Determine NuGet packages directories.
# Priority:
# 1) packageFolders declared in project.assets.json (handles custom nuget.config / multi-drive caches)
# 2) NUGET_PACKAGES environment variable override
# 3) Standard user profile fallback ~/.nuget/packages
$packageFolders = [System.Collections.Generic.List[string]]::new()
if ($assets.packageFolders) {
    foreach ($pfProp in $assets.packageFolders.PSObject.Properties) {
        if (-not [string]::IsNullOrWhiteSpace($pfProp.Name) -and -not $packageFolders.Contains($pfProp.Name)) {
            $packageFolders.Add($pfProp.Name)
        }
    }
}
if (-not [string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES) -and -not $packageFolders.Contains($env:NUGET_PACKAGES)) {
    $packageFolders.Add($env:NUGET_PACKAGES)
}
$defaultNuGet = Join-Path $env:USERPROFILE ".nuget\packages"
if (-not $packageFolders.Contains($defaultNuGet)) {
    $packageFolders.Add($defaultNuGet)
}

# Extract direct project dependencies
$directDepNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
if ($assets.project -and $assets.project.frameworks) {
    foreach ($fwProp in $assets.project.frameworks.PSObject.Properties) {
        if ($fwProp.Value.dependencies) {
            foreach ($depProp in $fwProp.Value.dependencies.PSObject.Properties) {
                $null = $directDepNames.Add($depProp.Name)
            }
        }
    }
}

$packageEntries = [System.Collections.Generic.List[PSObject]]::new()
$allStandardLicenseIds = [System.Collections.Generic.HashSet[string]]::new()
$encounteredPackageIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

# Diagnostics tracking
$diagTotalPackages = 0
$diagDeclaredLicenses = 0
$diagHeuristicLicenses = 0
$diagWithNotices = 0
$diagWarningsList = [System.Collections.Generic.List[string]]::new()

$totalLibs = @($libraries).Count
Write-Host "`nScanning and resolving package licenses & metadata ($totalLibs packages)..." -ForegroundColor Cyan

foreach ($lib in ($libraries | Sort-Object Name)) {
    $diagTotalPackages++
    $nameParts = $lib.Name -split '/'
    $packageId = $nameParts[0]
    $version = $nameParts[1]
    Write-Verbose "[$diagTotalPackages/$totalLibs] Inspecting $packageId ($version)..."
    $null = $encounteredPackageIds.Add($packageId)

    $isDirect = $directDepNames.Contains($packageId)
    $depType = if ($isDirect) { "Direct" } else { "Transitive" }
    $packageAnchor = "pkg-$((Get-Slug "$packageId-$version"))"

    $libPath = $lib.Value.path
    $packageDir = $null
    foreach ($folder in $packageFolders) {
        $candidate = Join-Path $folder $libPath
        if (Test-Path $candidate) {
            $packageDir = $candidate
            break
        }
    }
    if (-not $packageDir) {
        $packageDir = Join-Path $packageFolders[0] $libPath
        Write-Verbose "    [PKG-RESOLVE] Package path '$libPath' not found in any resolved package folders. Defaulted to: '$packageDir'"
    }

    $warnings = [System.Collections.Generic.List[string]]::new()
    $notes = [System.Collections.Generic.List[string]]::new()

    # 1. Inspect nuspec & files
    $nuspec = Get-NuspecMetadata -packageDir $packageDir -packageId $packageId
    if (-not $nuspec.HasNuspec) {
        $warnings.Add("Missing .nuspec file in package directory $packageDir")
    }

    $projectUrlResult = Resolve-PackageProjectUrl -packageId $packageId -nuspec $nuspec -overrides $PackageOverrides
    $projectUrl = $projectUrlResult.Url
    $projectUrlSource = $projectUrlResult.Source
    if ([string]::IsNullOrWhiteSpace($projectUrl)) {
        $warnings.Add("Missing projectUrl and repository.url")
    }

    $fileInspection = Get-PackageFileInfo -packageDir $packageDir -packageId $packageId -nuspec $nuspec

    # 2. Inspect GitHub repository if available
    $ghRepo = Get-GitHubRepoInfo -url $nuspec.RepositoryUrl -cacheDirectory $webCacheDir -offline:$Offline
    if (-not $ghRepo) { $ghRepo = Get-GitHubRepoInfo -url $nuspec.LicenseUrl -cacheDirectory $webCacheDir -offline:$Offline }
    if (-not $ghRepo) { $ghRepo = Get-GitHubRepoInfo -url $projectUrl -cacheDirectory $webCacheDir -offline:$Offline }

    $preferredGhFile = ""
    if (-not [string]::IsNullOrWhiteSpace($nuspec.LicenseUrl)) {
        if ($nuspec.LicenseUrl -match "github\.com/[^/]+/[^/]+/(?:blob|raw)/[^/]+/(?<file>.+)$") {
            $preferredGhFile = $Matches["file"]
        }
        elseif ($nuspec.LicenseUrl -match "raw\.githubusercontent\.com/[^/]+/[^/]+/[^/]+/(?<file>.+)$") {
            $preferredGhFile = $Matches["file"]
        }
    }

    $gitHubInfo = $null
    if ($ghRepo) {
        if ($ghRepo.FromPage) {
            $notes.Add("Discovered GitHub repository by scanning project page: https://github.com/$($ghRepo.Owner)/$($ghRepo.Repo)")
        }
        $gitHubInfo = Get-GitHubLicenseInfo -owner $ghRepo.Owner -repo $ghRepo.Repo -preferredFile $preferredGhFile -cacheDirectory $githubCacheDir -offline:$Offline
        if ($gitHubInfo.LicenseFound) {
            $notes.Add("Discovered license file on GitHub: $($ghRepo.Owner)/$($ghRepo.Repo)/HEAD/$($gitHubInfo.SourceFile)")
        }
    }

    # 3. Resolve copyright statement
    $cResult = Resolve-PackageCopyright -packageId $packageId -nuspec $nuspec -fileInspection $fileInspection `
        -gitHubInfo $gitHubInfo -ghRepo $ghRepo -overrides $PackageOverrides -notes $notes -warnings $warnings
    $finalCopyright = $cResult.Copyright
    $copyrightSource = $cResult.Source

    # 4. Resolve license
    $lResult = Resolve-PackageLicense -packageId $packageId -nuspec $nuspec -fileInspection $fileInspection `
        -gitHubInfo $gitHubInfo -ghRepo $ghRepo -overrides $PackageOverrides -warnings $warnings
    $primaryLicense = $lResult.PrimaryLicense
    $licenseSource = $lResult.LicenseSource
    $customLicenseText = $lResult.CustomLicenseText

    if ($lResult.IsDeclared) {
        $diagDeclaredLicenses++
    }
    else {
        $diagHeuristicLicenses++
    }

    # Register standard licenses
    if ($nuspec.LicenseType -eq "expression" -and -not [string]::IsNullOrWhiteSpace($nuspec.LicenseValue)) {
        # Tokenize SPDX expression: e.g. "MIT", "Apache-2.0 OR MIT", "(Apache-2.0 WITH LLVM-exception)", "GPL-2.0+"
        # Note: We filter out logical operators AND/OR/WITH as well as SPDX exception suffixes (e.g., -exception)
        # because standard license text URLs on spdx.org only exist for base license IDs, not compound exceptions.
        # We also strip trailing '+' (SPDX 2.0 "or later" notation) so the base license ID can be fetched and linked.
        $spdxTokens = [System.Text.RegularExpressions.Regex]::Split($nuspec.LicenseValue, "[\s\(\)]+")
        foreach ($token in $spdxTokens) {
            $cleaned = $token.Trim().TrimEnd('+')
            if ($cleaned -match "^[A-Za-z0-9\.\-]+$" -and $cleaned -notmatch "^(AND|OR|WITH)$" -and $cleaned -notmatch "-exception$") {
                $null = $allStandardLicenseIds.Add($cleaned)
            }
        }
    }
    $standardCandidates = @(
        "MIT", "MIT-0", "BSD-2-Clause", "BSD-3-Clause", "BSD-3-Clause-Clear", "BSD-4-Clause", "0BSD",
        "Apache-2.0", "Apache-1.1", "Apache-1.0", "ISC", "MPL-2.0", "MPL-1.1",
        "LGPL-2.1", "LGPL-2.1-only", "LGPL-2.1-or-later",
        "LGPL-3.0", "LGPL-3.0-only", "LGPL-3.0-or-later",
        "GPL-2.0", "GPL-2.0-only", "GPL-2.0-or-later",
        "GPL-3.0", "GPL-3.0-only", "GPL-3.0-or-later",
        "AGPL-1.0", "AGPL-1.0-only", "AGPL-1.0-or-later",
        "AGPL-3.0", "AGPL-3.0-only", "AGPL-3.0-or-later",
        "CC0-1.0", "CC-BY-4.0", "CC-BY-3.0", "CC-BY-2.5", "CC-BY-SA-4.0", "CC-BY-SA-3.0", "CC-BY-SA-2.5",
        "EUPL-1.1", "EUPL-1.2", "SSPL-1.0", "UPL-1.0", "Artistic-2.0", "CPOL-1.02", "AFL-3.0", "AFL-2.1", "NCSA",
        "Unlicense", "MS-PL", "MS-RL", "MS-LPL",
        "BSL-1.0", "EPL-1.0", "EPL-2.0", "Zlib", "PostgreSQL",
        "OFL-1.1", "OFL-1.0", "Unicode-DFS-2016", "CDDL-1.0", "CDDL-1.1",
        "OpenSSL", "curl", "ICU", "bzip2-1.0.6", "WTFPL", "JSON",
        "BlueOak-1.0.0", "W3C", "HPND", "SAX-PD", "Libpng-2.0",
        "CC-BY-NC-4.0", "CC-BY-NC-3.0", "CC-BY-ND-4.0", "CC-BY-ND-3.0"
    )
    foreach ($cand in $standardCandidates) {
        if ($primaryLicense -match "(^|[^a-zA-Z0-9_-])$([System.Text.RegularExpressions.Regex]::Escape($cand))([^a-zA-Z0-9_-]|$)") {
            $null = $allStandardLicenseIds.Add($cand)
        }
    }

    # 5. Resolve notices
    $nResult = Resolve-PackageNotices -packageId $packageId -fileInspection $fileInspection `
        -gitHubInfo $gitHubInfo -ghRepo $ghRepo -overrides $PackageOverrides
    $packageNotices = $nResult.PackageNotices
    $noticeSources = $nResult.NoticeSources
    $fullNoticesText = $nResult.FullNoticesText

    if ($packageNotices.Count -gt 0) {
        $diagWithNotices++
    }

    # 6. Structured logging
    Write-PackageStatusLog -packageId $packageId -version $version -depType $depType `
        -primaryLicense $primaryLicense -licenseSource $licenseSource `
        -finalCopyright $finalCopyright -copyrightSource $copyrightSource `
        -projectUrl $projectUrl -projectUrlSource $projectUrlSource `
        -packageNotices $packageNotices -noticeSources $noticeSources `
        -notes $notes -warnings $warnings -diagWarningsList $diagWarningsList

    $packageEntries.Add([PSCustomObject]@{
        Id                = $packageId
        Version           = $version
        IsDirect          = $isDirect
        DepType           = $depType
        PackageAnchor     = $packageAnchor
        Authors           = $nuspec.Authors
        Copyright         = $finalCopyright
        License           = $primaryLicense
        CustomLicenseText = $customLicenseText
        ProjectUrl        = $projectUrl
        ThirdPartyNotices = $fullNoticesText
    })
}

# Check for stale entries in $PackageOverrides
$allOverrideTables = @(
    @{ Name = "Licenses"; Table = $PackageOverrides.Licenses },
    @{ Name = "Copyrights"; Table = $PackageOverrides.Copyrights },
    @{ Name = "ProjectUrls"; Table = $PackageOverrides.ProjectUrls },
    @{ Name = "Notices"; Table = $PackageOverrides.Notices }
)
foreach ($ot in $allOverrideTables) {
    foreach ($key in $ot.Table.Keys) {
        if (-not $encounteredPackageIds.Contains($key)) {
            $diagWarningsList.Add("Stale override in `$PackageOverrides.$($ot.Name) for package '$key' (package not present in dependencies)")
        }
    }
}

# Group packages by identical ThirdPartyNotices content so that packages sharing the same notice
# text (e.g., multiple Microsoft.Extensions.* packages with the same NOTICES.txt) are collapsed
# into a single notice section in the output, avoiding redundant duplication.
$noticesGroups = [System.Collections.Generic.List[PSObject]]::new()
$processedPackages = [System.Collections.Generic.HashSet[string]]::new()
$noticeGroupIndex = 1

foreach ($entry in $packageEntries) {
    if ([string]::IsNullOrWhiteSpace($entry.ThirdPartyNotices)) { continue }
    if ($processedPackages.Contains($entry.Id)) { continue }

    $targetNotices = $entry.ThirdPartyNotices
    $matchingPackages = $packageEntries | Where-Object { $_.ThirdPartyNotices -eq $targetNotices }

    foreach ($m in $matchingPackages) {
        $null = $processedPackages.Add($m.Id)
    }

    $anchorId = "notices-group-$noticeGroupIndex"
    $noticeGroupIndex++

    $noticesGroups.Add([PSCustomObject]@{
        AnchorId   = $anchorId
        Packages   = $matchingPackages
        NoticeText = $targetNotices
    })
}

# Group packages by identical custom (non-standard) license text so that packages sharing the
# same custom license body are rendered once in the output with a shared "Applies to:" list.
$customLicenseGroups = [System.Collections.Generic.List[PSObject]]::new()
$processedCustomPackages = [System.Collections.Generic.HashSet[string]]::new()

foreach ($entry in $packageEntries) {
    if ([string]::IsNullOrWhiteSpace($entry.CustomLicenseText)) { continue }
    if ($processedCustomPackages.Contains($entry.Id)) { continue }

    $targetLicenseText = $entry.CustomLicenseText
    $matchingPackages = $packageEntries | Where-Object { $_.CustomLicenseText -eq $targetLicenseText }

    foreach ($m in $matchingPackages) {
        $null = $processedCustomPackages.Add($m.Id)
    }

    $customLicenseGroups.Add([PSCustomObject]@{
        LicenseName = $entry.License
        Packages    = $matchingPackages
        LicenseText = $targetLicenseText
    })
}

# Build final markdown content
$markdownContent = Build-ThirdPartyNoticesMarkdown -packageEntries $packageEntries `
    -noticesGroups $noticesGroups `
    -customLicenseGroups $customLicenseGroups `
    -allStandardLicenseIds $allStandardLicenseIds `
    -spdxCacheDir $spdxCacheDir `
    -offline:$Offline

# Verification mode (CI / pre-commit check)
if ($Verify) {
    if (-not (Test-Path $outputFile)) {
        Write-Error "Verification failed: $outputFile does not exist."
        exit 1
    }
    $existingContent = [System.IO.File]::ReadAllText($outputFile, [System.Text.Encoding]::UTF8)
    # Normalize CRLF to LF to prevent false-positive CI failures caused by cross-platform Git checkout differences
    $normNew = ($markdownContent -replace "`r?`n", "`n").Trim()
    $normExisting = ($existingContent -replace "`r?`n", "`n").Trim()
    if ($normNew -eq $normExisting) {
        Write-Host "`n[VERIFY OK] $outputFile is up to date!" -ForegroundColor Green
    }
    else {
        # Provide detailed diff diagnostics for CI / developer troubleshooting
        $newLines = $normNew -split "`n"
        $existLines = $normExisting -split "`n"
        Write-Host "`n[VERIFY FAILED] Differences detected between generated notices and $outputFile" -ForegroundColor Red
        Write-Host "  Existing file lines:  $($existLines.Count)" -ForegroundColor Yellow
        Write-Host "  Generated file lines: $($newLines.Count)" -ForegroundColor Yellow

        $maxCompare = [System.Math]::Min($newLines.Count, $existLines.Count)
        $diffCount = 0
        for ($i = 0; $i -lt $maxCompare; $i++) {
            if ($newLines[$i] -ne $existLines[$i]) {
                $diffLine = $i + 1
                Write-Host "  Divergence at line ${diffLine}:" -ForegroundColor Yellow
                Write-Host "    - Expected: $($existLines[$i])" -ForegroundColor Red
                Write-Host "    + Actual:   $($newLines[$i])" -ForegroundColor Green
                $diffCount++
                if ($diffCount -ge 3) {
                    Write-Host "  ... (additional differences truncated)" -ForegroundColor DarkGray
                    break
                }
            }
        }
        if ($diffCount -eq 0 -and $newLines.Count -ne $existLines.Count) {
            Write-Host "  Content matches up to line $maxCompare, but file length differs." -ForegroundColor Yellow
        }

        Write-Error "Verification failed: $outputFile is out of date. Run .\Setup\Update-ThirdPartyNotices.ps1 to update it."
        exit 1
    }
}
else {
    # Write UTF-8 without BOM consistently across all PowerShell versions
    [System.IO.File]::WriteAllText($outputFile, $markdownContent, [System.Text.UTF8Encoding]::new($false))
    $outItem = Get-Item $outputFile
    $lineCount = ($markdownContent -split "`r?`n").Count
    Write-Host "`nSuccessfully wrote $outputFile ($([math]::Round($outItem.Length / 1KB, 1)) KB, $lineCount lines)" -ForegroundColor Green
}

# -----------------------------------------------------------------------------
# 5. Final Execution Summary
# -----------------------------------------------------------------------------
$diagDirectPackages = ($packageEntries | Where-Object { $_.IsDirect }).Count
$diagTransitivePackages = ($packageEntries | Where-Object { -not $_.IsDirect }).Count

if ($scriptTimer) {
    $scriptTimer.Stop()
    $elapsedStr = $scriptTimer.Elapsed.ToString("mm\:ss\.fff")
}
else {
    $elapsedStr = "N/A"
}

Write-Host "`n===============================================================================" -ForegroundColor Cyan
Write-Host "THIRD-PARTY NOTICES GENERATION SUMMARY" -ForegroundColor Cyan
Write-Host "===============================================================================" -ForegroundColor Cyan
Write-Host "Total packages analyzed:         $diagTotalPackages"
Write-Host "  ├─ Direct dependencies:        $diagDirectPackages"
Write-Host "  ├─ Transitive dependencies:    $diagTransitivePackages"
Write-Host "  ├─ Explicit SPDX declarations: $diagDeclaredLicenses"
Write-Host "  ├─ Heuristic/Online/Mapped:    $diagHeuristicLicenses"
Write-Host "  ├─ Standard licenses tracked:  $($allStandardLicenseIds.Count)"
Write-Host "  ├─ Packages with notices:      $diagWithNotices"
$netErrorStr = if ($script:NetworkErrors -gt 0) { " | Network errors: $script:NetworkErrors" } else { "" }
Write-Host "  ├─ Network & Cache metrics:    SPDX ($script:SpdxCacheHits hits, $script:SpdxOnlineFetches fetches) | GitHub ($script:GitHubCacheHits hits, $script:GitHubOnlineFetches fetches) | Web ($script:WebCacheHits hits, $script:WebOnlineFetches fetches)$netErrorStr"
Write-Host "  ├─ Elapsed execution time:     $elapsedStr"
Write-Host "  └─ Warnings encountered:       $($diagWarningsList.Count)"

if ($script:NetworkErrorDetails.Count -gt 0) {
    Write-Host "`nNetwork failure details:" -ForegroundColor DarkYellow
    foreach ($err in $script:NetworkErrorDetails) {
        Write-Host "  [x] $err" -ForegroundColor DarkYellow
    }
}

if ($diagWarningsList.Count -gt 0) {
    Write-Host "`nWarnings to review:" -ForegroundColor Yellow
    foreach ($w in $diagWarningsList) {
        Write-Host "  [!] $w" -ForegroundColor Yellow
    }
    if ($FailOnWarning) {
        Write-Error "Terminating with failure due to $($diagWarningsList.Count) unresolved warning(s) (-FailOnWarning specified)."
        exit 1
    }
}
else {
    Write-Host "`nAll packages resolved cleanly with zero warnings!" -ForegroundColor Green
}
Write-Host "===============================================================================`n" -ForegroundColor Cyan
