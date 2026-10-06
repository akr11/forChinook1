using ChinookApi;
using ChinookApi.Checkers;

if (args.Contains("--worker"))
{
    return WorkerEntry.Run(args);
}

AppHost.Create(args).Run();
return 0;
