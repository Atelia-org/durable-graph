using Atelia.DurableGraph.Cli;

using var cancellation = new CancellationTokenSource();
ConsoleCancelEventHandler cancel = (_, e) => {
    e.Cancel = true;
    cancellation.Cancel();
};
Console.CancelKeyPress += cancel;
try {
    return MaintenanceCli.Run(args, Console.Out, Console.Error, cancellation.Token);
} finally {
    Console.CancelKeyPress -= cancel;
}
