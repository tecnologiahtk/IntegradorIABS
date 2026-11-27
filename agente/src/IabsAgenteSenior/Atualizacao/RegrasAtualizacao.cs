using System.Text.Json;
using System.Text.RegularExpressions;
using IabsAgenteSenior.Configuracao;

namespace IabsAgenteSenior.Atualizacao;

/// <summary>O pedido que o integrador deixa para o atualizador (atualizar.json na pasta de dados).</summary>
public sealed record PedidoAtualizacao(string Versao, string Sha256, DateTimeOffset PedidoEm);

/// <summary>Como ficou a última atualização: vai no "ola" para a IABS ver.</summary>
public sealed record SituacaoAtualizacao(string Versao, string Situacao, string? Codigo, DateTimeOffset Em);

/// <summary>
/// Regras da atualização (2.1.0): só versão mais nova, só do repositório
/// oficial, só com o SHA-256 que a IABS mandou batendo com o do GitHub e com
/// o do arquivo baixado.
/// </summary>
public static partial class RegrasAtualizacao
{
    [GeneratedRegex(@"^\d{1,3}\.\d{1,3}\.\d{1,4}$")]
    private static partial Regex FormatoVersao();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex FormatoSha256();

    public static bool VersaoValida(string? v) => v is not null && FormatoVersao().IsMatch(v);

    public static bool Sha256Valido(string? s) => s is not null && FormatoSha256().IsMatch(s);

    /// <summary>A versão pedida é mais nova que a instalada?</summary>
    public static bool MaisNova(string pedida, string atual) =>
        VersaoValida(pedida) && Version.TryParse(atual, out var a) && Version.Parse(pedida) > a;

    /// <summary>Onde o instalador da versão está publicado (nome fixo em todas as versões).</summary>
    public static Uri UrlDoInstalador(string versao) =>
        new($"https://github.com/{Constantes.RepositorioOficial}/releases/download/v{versao}/{Constantes.ArquivoInstalador}");

    /// <summary>A versão na API do GitHub (de onde sai o SHA-256 do GitHub).</summary>
    public static Uri ApiDaVersao(string versao) =>
        new($"https://api.github.com/repos/{Constantes.RepositorioOficial}/releases/tags/v{versao}");

    /// <summary>Só HTTPS, só GitHub: a página da versão, a API e o armazenamento dos arquivos (para onde o download redireciona).</summary>
    public static bool HostPermitido(Uri u) =>
        u.Scheme == Uri.UriSchemeHttps
        && (u.Host == "github.com" || u.Host == "api.github.com" || u.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>O SHA-256 do instalador na resposta da API do GitHub (campo digest do asset); nulo se não houver.</summary>
    public static (string? Sha256, string? Url) LerVersaoDoGitHub(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return (null, null);
        foreach (var a in assets.EnumerateArray())
        {
            if (a.TryGetProperty("name", out var n) && n.GetString() == Constantes.ArquivoInstalador)
            {
                var digest = a.TryGetProperty("digest", out var d) ? d.GetString() : null;
                var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                var sha = digest is not null && digest.StartsWith("sha256:", StringComparison.Ordinal) ? digest["sha256:".Length..].ToLowerInvariant() : null;
                return (sha, url);
            }
        }
        return (null, null);
    }
}

/// <summary>
/// Os arquivos da atualização na pasta de dados, gravados só pelo integrador e
/// pela janela (nunca pelo atualizador, que roda como SYSTEM: AreaDoSistema).
/// O pedido (atualizar.json) é lido pelo atualizador; a situação "PEDIDA"
/// (atualizacao.json) é a do pedido, e a do atualizador fica no registro.
/// </summary>
public static class ArquivosAtualizacao
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string Pedido(Caminhos c) => Path.Combine(c.PastaDados, "atualizar.json");
    public static string Situacao(Caminhos c) => Path.Combine(c.PastaDados, "atualizacao.json");

    public static void GravarPedido(Caminhos c, PedidoAtualizacao p) => Gravar(Pedido(c), p);

    public static PedidoAtualizacao? LerPedido(Caminhos c) => Ler<PedidoAtualizacao>(Pedido(c));

    public static void GravarSituacao(Caminhos c, SituacaoAtualizacao s) => Gravar(Situacao(c), s);

    /// <summary>A situação mais recente: a do pedido (pasta de dados) ou a do atualizador (registro).</summary>
    public static SituacaoAtualizacao? LerSituacao(Caminhos c, IGuardaDaAtualizacao? sistema = null)
    {
        var doPedido = Ler<SituacaoAtualizacao>(Situacao(c));
        var doAtualizador = (sistema ?? new GuardaNoRegistro()).Ler();
        if (doPedido is null) return doAtualizador;
        if (doAtualizador is null) return doPedido;
        return doAtualizador.Em >= doPedido.Em ? doAtualizador : doPedido;
    }

    private static void Gravar<T>(string arquivo, T valor)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(arquivo)!);
        var temporario = arquivo + ".novo";
        File.WriteAllText(temporario, JsonSerializer.Serialize(valor, Json));
        File.Move(temporario, arquivo, overwrite: true);
    }

    private static T? Ler<T>(string arquivo) where T : class
    {
        try
        {
            return File.Exists(arquivo) ? JsonSerializer.Deserialize<T>(File.ReadAllText(arquivo), Json) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
