using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace IppPrinter.Services;

public interface IDocumentConverter
{
    bool CanConvert(string mimeType);
    Task<int> ConvertAsync(byte[] input, Stream output, string? mediaName = null, CancellationToken cancellationToken = default);
}
