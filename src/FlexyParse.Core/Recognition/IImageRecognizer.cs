using FlexyParse.Model;
using FlexyParse.Storage;

namespace FlexyParse.Recognition;

public interface IImageRecognizer
{
    Task RecognizeAsync(JobWorkspace workspace, DocumentIndex index, CancellationToken cancellationToken);
}
