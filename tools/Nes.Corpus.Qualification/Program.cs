using System.Text;

namespace Nes.Corpus.Qualification;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args is ["worker"])
        {
            Console.SetError(TextWriter.Null);
            var workerResult = await QualificationWorker.RunAsync(Console.OpenStandardInput()).ConfigureAwait(false);
            var bytes = Encoding.UTF8.GetBytes(WorkerProtocol.SerializeResult(workerResult) + "\n");
            await Console.OpenStandardOutput().WriteAsync(bytes).ConfigureAwait(false);
            return workerResult.Passed ? 0 : 1;
        }

        var parsed = QualificationCommandLine.Parse(args);
        if (!parsed.IsSuccess)
        {
            return 2;
        }

        QualificationRun run;
        try
        {
            run = await QualificationCoordinator.RunAsync(parsed.Options!, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            run = QualificationCoordinator.CreateClosedFailure(parsed.Options!);
        }

        var report = Encoding.UTF8.GetBytes(AggregateJson.Serialize(run.Report) + "\n");
        await Console.OpenStandardOutput().WriteAsync(report).ConfigureAwait(false);
        return run.Succeeded ? 0 : 1;
    }
}
