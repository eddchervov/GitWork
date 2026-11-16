using GitWork.Core;
using System.IO;

namespace GitWork.App;

internal sealed class PreparationLogProgress(PreparationJournal journal, PreparationProfile profile,
    IProgress<PreparationOutput> display, Action stop) : IProgress<PreparationOutput>
{
    private Exception? _error;

    public Exception? Error => Volatile.Read(ref this._error);

    public void Report(PreparationOutput value)
    {
        if (this.Error is null)
        {
            try
            {
                journal.Report(value, profile);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (Interlocked.CompareExchange(ref this._error, exception, null) is null)
                {
                    stop();
                }
            }
        }

        display.Report(value);
    }
}
