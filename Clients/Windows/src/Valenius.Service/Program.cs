using Microsoft.Extensions.Logging.EventLog;
using Valenius.Service;

IHost host = Host.CreateDefaultBuilder(args)
    .UseWindowsService(options =>
    {
        options.ServiceName = "Valenius";
    })
    // CreateDefaultBuilder adds an EventLog provider on Windows by default, but its default
    // source name is ".NET Runtime" — every LogWarning/LogError in this service was silently
    // landing there instead of under "Valenius". DiagnosticsCollector reads the Application log
    // filtered to source "Valenius" (the same source the Windows Service host itself uses for its
    // own start/stop notifications), so without this, every diagnostic bundle ever collected was
    // blind to our own error/warning logs — only those two hardcoded lifecycle lines ever showed
    // up. Must match the ServiceName above exactly.
    .ConfigureLogging(logging =>
    {
        if (OperatingSystem.IsWindows())
        {
            logging.AddEventLog(settings => settings.SourceName = "Valenius");
            // CreateDefaultBuilder also installs its own filter restricting EventLogLoggerProvider
            // to Warning-and-above — which silently swallows every LogInformation call (e.g. "Client
            // config repair requested by admin...", "self-heal succeeded") regardless of source name.
            // That makes a repair that ran-and-succeeded, ran-and-found-nothing-to-fix, and never-ran
            // indistinguishable to whoever's reading the Event Log — the opposite of what the
            // diagnostics/repair features exist for. Override it back down to Information, but only
            // for OUR OWN categories ("Valenius.Service.*") — a category-scoped rule is more
            // specific than CreateDefaultBuilder's own unscoped ">= Warning" rule and so takes
            // precedence for those categories, while everything else (in particular the framework's
            // own very chatty per-request System.Net.Http.HttpClient.* logging, which fires 4 lines
            // per long-poll request every ~5s) stays at the original Warning-and-above default.
            // Learned the hard way: an earlier version of this fix used the unscoped overload and
            // flooded a 5-minute Event Log capture with ~3300 lines of HTTP tracing, burying the one
            // line that actually mattered.
            logging.AddFilter<EventLogLoggerProvider>("Valenius.Service", LogLevel.Information);
        }
    })
    .ConfigureAppConfiguration((_, config) =>
    {
        config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: false);
    })
    .ConfigureServices((hostContext, services) =>
    {
        services.AddSingleton<TunnelStateManager>();
        services.AddSingleton<WireGuardController>();
        services.AddSingleton<ConfigManager>();
        services.AddSingleton<RegistrationManager>();
        services.AddSingleton<AutoConnectManager>();

        // The X-Api-Key is injected per-request by ApiKeyHandler from ApiKeyProvider (rather
        // than baked into DefaultRequestHeaders at startup) so a backend key rotation delivered
        // in the heartbeat takes effect on the very next request. See ApiKeyProvider.
        services.AddSingleton<ApiKeyProvider>();
        services.AddTransient<ApiKeyHandler>();

        // Holds the backend server URL. Falls back to appsettings, but a persisted user choice
        // (set via the tray first-run prompt when the installer provided no URL) always wins.
        services.AddSingleton<BackendUrlProvider>();

        services.AddHttpClient("Update");          // anonymous client for update manifest + installer download
        services.AddSingleton<UpdateChecker>();    // singleton so ClientRegistrationService can trigger it
        services.AddHttpClient("Registration")
            .AddHttpMessageHandler<ApiKeyHandler>();
        // Separate client for long-poll: explicit 70-second timeout to outlast the
        // server's 55-second hold without the default 100-second global timeout causing
        // premature cancellation on slow networks.
        services.AddHttpClient("LongPoll", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(70);
        }).AddHttpMessageHandler<ApiKeyHandler>();

        services.AddSingleton<ClientRegistrationService>();
        services.AddSingleton<ConnectivityVerifier>();
        services.AddSingleton<HandshakeVerifier>();

        // Registered FIRST so its StartAsync runs before any other hosted service — repairs the
        // data-dir ACLs (if a bad install left them unreadable) before the heartbeat/config code runs.
        services.AddHostedService<DataDirHealthService>();

        services.AddHostedService<PipeServer>();
        services.AddHostedService(sp => sp.GetRequiredService<UpdateChecker>());    // uses singleton
        services.AddHostedService(sp => sp.GetRequiredService<ClientRegistrationService>());
        services.AddHostedService<AutoConnectService>();
        services.AddHostedService<TrayLaunchService>();
    })
    .Build();

host.Run();
