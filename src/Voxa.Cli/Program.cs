using Voxa.Cli;

// Thin entry point — all logic lives in the testable VoxaCliRunner.
return await VoxaCliRunner.RunAsync(args, Console.Out, Console.Error).ConfigureAwait(false);
