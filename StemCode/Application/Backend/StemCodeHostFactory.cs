using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StemCode.Application.Abstractions;
using StemCode.Application.Commands;
using StemCode.Application.DependencyInjection;
using StemCode.Application.UI;
using StemCode.Infrastructure.DependencyInjection;

namespace StemCode.Application.Backend;

public static class StemCodeHostFactory
{
    public static IHost Create(
        IUiBridge uiBridge,
        string[] args)
    {
        return Create(
            uiBridge,
            BackendRuntimeArguments.Parse(args),
            [],
            autoApproveAllTools: false);
    }

    public static IHost Create(
        IUiBridge uiBridge,
        string[] args,
        IReadOnlyList<BackendMcpServerConfiguration>? sessionMcpServers)
    {
        return Create(
            uiBridge,
            BackendRuntimeArguments.Parse(args),
            sessionMcpServers,
            autoApproveAllTools: false);
    }

    public static IHost Create(
        IUiBridge uiBridge,
        string[] args,
        IReadOnlyList<BackendMcpServerConfiguration>? sessionMcpServers,
        bool autoApproveAllTools)
    {
        return Create(
            uiBridge,
            BackendRuntimeArguments.Parse(args),
            sessionMcpServers,
            autoApproveAllTools);
    }

    internal static IHost Create(
        IUiBridge uiBridge,
        BackendRuntimeArguments runtimeArguments,
        IReadOnlyList<BackendMcpServerConfiguration>? sessionMcpServers,
        bool autoApproveAllTools,
        Action<IServiceCollection>? configureServices = null)
    {
        ArgumentNullException.ThrowIfNull(uiBridge);
        ArgumentNullException.ThrowIfNull(runtimeArguments);

        string currentDirectory = Directory.GetCurrentDirectory();
        HostApplicationBuilder builder = new(new HostApplicationBuilderSettings
        {
            ApplicationName = "StemCode",
            ContentRootPath = currentDirectory,
            DisableDefaults = true
        });

        builder.Configuration.AddJsonFile(
            Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
            optional: true,
            reloadOnChange: false);

        builder.Configuration.AddJsonFile(
            Path.Combine(currentDirectory, ".stemcode", "agent-profile.json"),
            optional: true,
            reloadOnChange: false);
        builder.Configuration.AddEnvironmentVariables();
        builder.Configuration.AddCommandLine(runtimeArguments.RawArgs);

        builder.Logging.ClearProviders();
        builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));

        builder.Services.AddSingleton(uiBridge);
        builder.Services.AddSingleton(sessionMcpServers ?? []);
        builder.Services.AddSingleton(new BackendRuntimeOptions(
            sessionMcpServers,
            autoApproveAllTools,
            runtimeArguments.EffectiveAppSurface(BackendRuntimeOptions.CliSurface),
            ResolveStartupPromptPreference(runtimeArguments.RawArgs)));
        builder.Services
            .AddApplication()
            .AddReplCommands()
            .AddInfrastructure(builder.Configuration);

        builder.Services.AddSingleton<ISelectionPrompt, UiSelectionPrompt>();
        builder.Services.AddSingleton<ITextPrompt, UiTextPrompt>();
        builder.Services.AddSingleton<ISecretPrompt, UiSecretPrompt>();
        builder.Services.AddSingleton<IConfirmationPrompt, UiConfirmationPrompt>();
        builder.Services.AddSingleton<IStatusMessageWriter, UiStatusMessageWriter>();

        // SDK/embedder extension point: runs after Application + Infrastructure + the
        // default UI prompt registrations so callers can override services (for example
        // an in-memory provider configuration to skip interactive onboarding, a fixed
        // workspace root, or custom ITool registrations) and add their own.
        builder.Services.AddSingleton(runtimeArguments.Overrides);

        configureServices?.Invoke(builder.Services);

        IHost host = builder.Build();
        ApplyHeadlessPermissionRules(host.Services, runtimeArguments.Overrides);
        return host;
    }

    // --allowedTools / --disallowedTools become ordinary permission rules appended
    // after the configured ones, so they win over config and show up in /rules,
    // while a later session /allow or /deny can still override them.
    private static void ApplyHeadlessPermissionRules(
        IServiceProvider services,
        Models.HeadlessRunOverrides overrides)
    {
        IReadOnlyList<Models.PermissionRule> rules = overrides.CreatePermissionRules();
        if (rules.Count == 0)
        {
            return;
        }

        Models.PermissionSettings? settings = services.GetService<Models.PermissionSettings>();
        if (settings is null)
        {
            return;
        }

        settings.Rules = [.. settings.Rules ?? [], .. rules];
    }

    private static bool ResolveStartupPromptPreference(IReadOnlyList<string> args)
    {
        for (int index = 0; index < args.Count; index++)
        {
            if (TryReadOptionValue(args, ref index, "--startup-prompts", out string? value))
            {
                return string.Equals(value, "enabled", StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;
    }

    private static bool TryReadOptionValue(
        IReadOnlyList<string> args,
        ref int index,
        string optionName,
        out string? value)
    {
        string arg = args[index];
        value = null;

        if (string.Equals(arg, optionName, StringComparison.OrdinalIgnoreCase))
        {
            int valueIndex = index + 1;
            if (valueIndex >= args.Count || string.IsNullOrWhiteSpace(args[valueIndex]))
            {
                return false;
            }

            value = args[valueIndex].Trim();
            index = valueIndex;
            return true;
        }

        string prefix = optionName + "=";
        if (!arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        value = arg[prefix.Length..].Trim();
        return !string.IsNullOrWhiteSpace(value);
    }
}
