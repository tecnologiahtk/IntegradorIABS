using System.Net;
using IabsAgenteSenior.Configuracao;

namespace IabsAgenteSenior.Canal;

/// <summary>
/// Saída para a IABS: só HTTPS (wss), para o domínio configurado, com o proxy e
/// os certificados do Windows (funciona atrás de proxy com inspeção de TLS).
/// </summary>
public static class Rede
{
    /// <summary>Endereço do canal a partir do endereço da IABS: só o domínio conta.</summary>
    public static Uri UriDoCanal(string enderecoIabs)
    {
        if (!Uri.TryCreate(enderecoIabs.Trim(), UriKind.Absolute, out var u))
        {
            throw new InvalidOperationException("Endereço da IABS inválido. Use o endereço completo, começando com https://.");
        }
        var local = u.IsLoopback;
        var esquema = u.Scheme switch
        {
            "https" => "wss",
            "http" when local => "ws",
            "http" => throw new InvalidOperationException("O endereço da IABS precisa começar com https:// (a chave do integrador vai no cabeçalho)."),
            _ => throw new InvalidOperationException("O endereço da IABS precisa começar com https://."),
        };
        return new UriBuilder(esquema, u.Host, u.Port, "/api/integrador/canal").Uri;
    }

    public static SocketsHttpHandler CriarHandler(ConfiguracaoProxy proxy)
    {
        var h = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(20),
        };
        switch (proxy.Modo.ToLowerInvariant())
        {
            case "nenhum":
                h.UseProxy = false;
                break;
            case "manual" when !string.IsNullOrWhiteSpace(proxy.Endereco):
                h.UseProxy = true;
                h.Proxy = new WebProxy(proxy.Endereco)
                {
                    Credentials = proxy.Usuario is null
                        ? CredentialCache.DefaultCredentials
                        : new NetworkCredential(proxy.Usuario, string.IsNullOrEmpty(proxy.SenhaProtegida) ? "" : Protetor.Abrir(proxy.SenhaProtegida)),
                };
                break;
            default: // sistema
                h.UseProxy = true;
                h.DefaultProxyCredentials = CredentialCache.DefaultCredentials;
                break;
        }
        return h;
    }
}
