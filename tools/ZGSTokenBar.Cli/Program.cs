if (string.Equals(
        Environment.GetEnvironmentVariable("ZGSTOKENBAR_PLUGIN_ID"),
        "test.process-fixture",
        StringComparison.Ordinal))
{
    return await ZGSTokenBar.Cli.ProcessPluginFixture.RunAsync();
}

return await ZGSTokenBar.Cli.CliApplication.RunAsync(args);
