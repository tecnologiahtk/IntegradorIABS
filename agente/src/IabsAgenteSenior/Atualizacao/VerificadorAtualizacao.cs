using System.ServiceProcess;
using System.Text.Json;
using IabsAgenteSenior.Canal;
using IabsAgenteSenior.Configuracao;

namespace IabsAgenteSenior.Atualizacao;

/// <summary>A última versão publicada no repositório oficial.</summary>
public sealed record VersaoPublicada(string Versao, string Sha256);

/// <summary>
/// "Verificar atualização" e "Atualizar agora" da janela (2.1.0). A janela
/// consulta a última versão do repositório oficial e, se a TI pedir, deixa o
/// pedido com o SHA-256 publicado no GitHub e liga o atualizador, que confere
/// de novo (GitHub e arquivo) antes de instalar. Pela tela da IABS o pedido
/// traz também o SHA-256 da IABS.
/// </summary>
public static class VerificadorAtualizacao
{
    /// <summary>A última versão publicada (null = sem versão com instalador); erro de rede sobe.</summary>
    public static async Task<VersaoPublicada?> UltimaVersaoAsync(ConfiguracaoProxy proxy, CancellationToken ct, HttpMessageHandler? handler = null)
    {
        using var http = new HttpClient(handler ?? Rede.CriarHandler(proxy), disposeHandler: handler is null) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(Constantes.UserAgent);
        var url = new Uri($"https://api.github.com/repos/{Constantes.RepositorioOficial}/releases/latest");
        var json = await http.GetStringAsync(url, ct);
        using var doc = JsonDocument.Parse(json);
        var tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        var versao = tag is { Length: > 1 } && tag[0] == 'v' ? tag[1..] : null;
        if (!RegrasAtualizacao.VersaoValida(versao)) return null;
        var (sha, endereco) = RegrasAtualizacao.LerVersaoDoGitHub(json);
        if (sha is null || endereco != RegrasAtualizacao.UrlDoInstalador(versao!).ToString()) return null;
        return new VersaoPublicada(versao!, sha);
    }

    /// <summary>
    /// Botão Atualizar da janela (2.2.1): só liga o atualizador, sem gravar nada
    /// na pasta de dados. Ligado sem pedido, ele mesmo pega no GitHub a última
    /// versão e o SHA-256 dela. O instalador deixa qualquer usuário logado
    /// ligá-lo (só ligar: parar e configurar continuam com administradores).
    /// </summary>
    public static void LigarAtualizador(VersaoPublicada v)
    {
        if (!RegrasAtualizacao.MaisNova(v.Versao, Constantes.Versao)) throw new InvalidOperationException("A versão instalada já é a mais nova.");
        using var sc = new ServiceController(Constantes.NomeServicoAtualizador);
        if (sc.Status == ServiceControllerStatus.Stopped) sc.Start();
    }
}
