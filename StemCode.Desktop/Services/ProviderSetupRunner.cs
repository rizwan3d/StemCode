using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StemCode.Application.Abstractions;
using StemCode.Application.Backend;
using StemCode.Application.Models;
using StemCode.Desktop.Models;
using StemCode.Domain.Models;

namespace StemCode.Desktop.Services;

public sealed class ProviderSetupRunner : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<string>? _currentActivity;

    public event EventHandler<DesktopSelectionPrompt?>? SelectionPromptChanged;

    public event EventHandler<DesktopTextPrompt?>? TextPromptChanged;

    public async Task<ProviderSetupRunResult> RunAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            List<string> activity = [];
            _currentActivity = activity;

            DesktopUiBridge bridge = new(
                AddBridgeActivity,
                sink: null,
                SetSelectionPrompt,
                SetTextPrompt);

            IHost? host = null;
            try
            {
                host = StemCodeHostFactory.Create(bridge, []);
                IProviderSetupService setupService = host.Services.GetRequiredService<IProviderSetupService>();
                ProviderSetupResult setupResult = await setupService.EnsureConfiguredAsync(cancellationToken);

                string providerName = setupResult.OnboardingResult.Profile.ProviderKind.ToDisplayName();
                string modelId = setupResult.ModelDiscoveryResult.SelectedModelId;
                activity.Add($"Provider ready: {providerName} / {modelId}");

                return new ProviderSetupRunResult(providerName, modelId, activity);
            }
            finally
            {
                if (host is IAsyncDisposable asyncDisposable)
                {
                    await asyncDisposable.DisposeAsync();
                }
                else
                {
                    host?.Dispose();
                }
            }
        }
        finally
        {
            _currentActivity = null;
            SetSelectionPrompt(null);
            SetTextPrompt(null);
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }

    private void AddBridgeActivity(string message)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            _currentActivity?.Add(message.Trim());
        }
    }

    private void SetSelectionPrompt(DesktopSelectionPrompt? prompt)
    {
        SelectionPromptChanged?.Invoke(this, prompt);
    }

    private void SetTextPrompt(DesktopTextPrompt? prompt)
    {
        TextPromptChanged?.Invoke(this, prompt);
    }
}

public sealed record ProviderSetupRunResult(
    string ProviderName,
    string ModelId,
    IReadOnlyList<string> Activity);
