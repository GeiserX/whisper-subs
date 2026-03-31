using System.Threading;
using System.Threading.Tasks;

namespace WhisperSubs.Providers
{
    public interface ISubtitleProvider
    {
        string Name { get; }
        Task<string> TranscribeAsync(string audioPath, string language, CancellationToken cancellationToken);
    }
}
