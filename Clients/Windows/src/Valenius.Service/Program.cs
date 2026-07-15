using Valenius.Service;

IHost host = Host.CreateDefaultBuilder(args)
    .UseWindowsService(options =>
    {
        options.ServiceName = "Valenius";
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
