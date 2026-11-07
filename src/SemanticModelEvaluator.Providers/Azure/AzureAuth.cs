using Azure.Core;
using Azure.Identity;
using SemanticModelEvaluator.Core.Providers;

namespace SemanticModelEvaluator.Providers.Azure;

/// <summary>Which established Azure identity flow to use. No mode silently falls back to a different identity.</summary>
public enum AzureIdentityMode
{
    /// <summary>Azure CLI sign-in (<c>az login</c>) — the mechanism used by EvalKit.Viewer.</summary>
    AzureCli,
    /// <summary>DefaultAzureCredential with interactive browser disabled (environment, workload/managed identity, VS, CLI...).</summary>
    Default
}

/// <summary>Bearer-token source. Abstracted so tests never contact Entra ID.</summary>
public interface IAzureTokenSource
{
    Task<string> GetTokenAsync(string scope, CancellationToken ct);
    TokenCredential? Credential { get; }
}

public sealed class AzureTokenSource(AzureIdentityMode mode, string? tenantId = null) : IAzureTokenSource
{
    private TokenCredential? credential;

    public TokenCredential Credential => credential ??= mode switch
    {
        AzureIdentityMode.AzureCli => new AzureCliCredential(new AzureCliCredentialOptions
        {
            TenantId = string.IsNullOrWhiteSpace(tenantId) ? null : tenantId,
            ProcessTimeout = TimeSpan.FromMinutes(1)
        }),
        _ => new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            TenantId = string.IsNullOrWhiteSpace(tenantId) ? null : tenantId,
            ExcludeInteractiveBrowserCredential = true
        })
    };

    TokenCredential? IAzureTokenSource.Credential => Credential;

    public async Task<string> GetTokenAsync(string scope, CancellationToken ct)
    {
        try
        {
            var token = await Credential.GetTokenAsync(new TokenRequestContext([scope]), ct).ConfigureAwait(false);
            return token.Token;
        }
        catch (CredentialUnavailableException ex)
        {
            throw new AccessDeniedException("Azure identity is unavailable. " + Hint + " Detail: " + Core.Security.Redactor.Redact(ex.Message), ex);
        }
        catch (AuthenticationFailedException ex)
        {
            throw new AccessDeniedException("Azure sign-in failed or expired. " + Hint, ex);
        }
    }

    private string Hint => mode == AzureIdentityMode.AzureCli
        ? "Run 'az login' (add --tenant <tenant-id> if your resources are in another tenant) and retry."
        : "Sign in with Visual Studio or 'az login', or configure a managed identity, then retry.";
}
