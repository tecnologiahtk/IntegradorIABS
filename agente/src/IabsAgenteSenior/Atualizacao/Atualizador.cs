using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using IabsAgenteSenior.Canal;
using IabsAgenteSenior.Configuracao;
using Microsoft.Extensions.Logging;

namespace IabsAgenteSenior.Atualizacao;

/// <summary>
/// O atualizador (2.1.0): serviço separado, como SYSTEM, que o integrador
/// liga quando a IABS pede uma versão nova. Ele:
///   1. lê o pedido (versão e SHA-256 que a IABS mandou); pedido já tratado
///      não roda de novo (um pedido, uma tentativa). Ligado sem pedido novo
///      (2.2.1: o botão Atualizar da janela, que qualquer usuário logado pode
///      usar), pega na API do GitHub a última versão publicada e o SHA-256
///      dela: quem liga não escolhe versão nem arquivo;
///   2. só segue com versão mais nova que a instalada;
///   3. pega na API do GitHub o SHA-256 do instalador daquela versão, no
///      repositório oficial: tem de ser igual ao da IABS;
///   4. baixa o instalador (só HTTPS, só GitHub, até 300 MB) numa pasta só de
///      SYSTEM e administradores (AreaDoSistema) e confere o SHA-256 do
///      arquivo: tem de ser igual aos dois;
///   5. confere o SHA-256 de novo logo antes, roda o msiexec com esse
///      arquivo, sem janela, e termina. O instalador para o integrador, troca
///      os arquivos e liga de novo.
/// Para instalar algo falso, alguém teria de adulterar o GitHub E a IABS.
/// Nada é gravado nem apagado na pasta de dados (revisão de 27/11/2026): a
/// situação de cada passo fica no registro, que o integrador manda à IABS.
/// </summary>
public sealed class Atualizador(
    Caminhos caminhos,
    ILogger logger,
    HttpMessageHandler? handler = null,
    Func<ProcessStartInfo, bool>? iniciarProcesso = null,
    AreaDoSistema? area = null)
{
    private const long TamanhoMaximo = 300L * 1024 * 1024;
    private readonly AreaDoSistema _area = area ?? AreaDoSistema.Padrao();

    public async Task<SituacaoAtualizacao> ExecutarAsync(CancellationToken ct)
    {
        var pedido = ArquivosAtualizacao.LerPedido(caminhos);
        // um pedido, uma tentativa: o atualizador não apaga o pedido (fica na
        // pasta de dados), marca no registro que já tratou este
        if (pedido is not null && _area.Guarda.PedidoTratado() != pedido.PedidoEm)
        {
            try
            {
                _area.Guarda.MarcarPedidoTratado(pedido.PedidoEm);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return Registrar(pedido.Versao, "ERRO", "PEDIDO_PRESO");
            }
            return await InstalarAsync(pedido.Versao, pedido.Sha256, ct);
        }
        return await InstalarUltimaAsync(ct);
    }

    /// <summary>
    /// Ligado sem pedido novo da IABS (2.2.1): o botão Atualizar da janela, que
    /// qualquer usuário logado pode usar. Só a última versão publicada no
    /// repositório oficial e mais nova que a instalada, com o SHA-256 do
    /// GitHub, conferido de novo na API da versão e no arquivo.
    /// </summary>
    private async Task<SituacaoAtualizacao> InstalarUltimaAsync(CancellationToken ct)
    {
        VersaoPublicada? ultima;
        try
        {
            ultima = await VerificadorAtualizacao.UltimaVersaoAsync(ProxyDaMaquina(), ct, handler);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return Registrar("última", "ERRO", "GITHUB_INDISPONIVEL");
        }
        if (ultima is null) return Registrar("última", "ERRO", "VERSAO_SEM_INSTALADOR");
        if (!RegrasAtualizacao.MaisNova(ultima.Versao, Constantes.Versao))
        {
            logger.LogInformation("Atualizador ligado sem pedido: a versão instalada ({Versao}) já é a mais nova.", Constantes.Versao);
            return new SituacaoAtualizacao(ultima.Versao, "SEM_PEDIDO", "JA_E_A_MAIS_NOVA", DateTimeOffset.Now);
        }
        logger.LogInformation("Atualizador ligado sem pedido da IABS: instalando a última versão publicada ({Versao}).", ultima.Versao);
        return await InstalarAsync(ultima.Versao, ultima.Sha256, ct);
    }

    private ConfiguracaoProxy ProxyDaMaquina()
    {
        try
        {
            return ConfiguracaoIntegrador.Carregar(caminhos)?.ProxyPrincipal ?? new ConfiguracaoProxy();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return new ConfiguracaoProxy();
        }
    }

    /// <summary>Passos 2 a 5: a versão e o SHA-256 esperados (o da IABS, ou o do GitHub quando quem liga é a janela).</summary>
    private async Task<SituacaoAtualizacao> InstalarAsync(string versao, string sha, CancellationToken ct)
    {
        if (!RegrasAtualizacao.VersaoValida(versao) || !RegrasAtualizacao.Sha256Valido(sha)) return Registrar(versao, "ERRO", "PEDIDO_INVALIDO");
        if (!RegrasAtualizacao.MaisNova(versao, Constantes.Versao)) return Registrar(versao, "ERRO", "VERSAO_NAO_E_MAIS_NOVA");

        Registrar(versao, "BAIXANDO", null);
        using var http = CriarHttp();
        string? shaGitHub;
        string? urlGitHub;
        try
        {
            using var r = await PegarAsync(http, RegrasAtualizacao.ApiDaVersao(versao), ct);
            if (!r.IsSuccessStatusCode) return Registrar(versao, "ERRO", r.StatusCode == HttpStatusCode.NotFound ? "VERSAO_NAO_PUBLICADA" : "GITHUB_INDISPONIVEL");
            (shaGitHub, urlGitHub) = RegrasAtualizacao.LerVersaoDoGitHub(await r.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return Registrar(versao, "ERRO", "GITHUB_INDISPONIVEL");
        }
        if (shaGitHub is null) return Registrar(versao, "ERRO", "VERSAO_SEM_INSTALADOR");
        if (shaGitHub != sha) return Registrar(versao, "ERRO", "SHA256_DIVERGENTE_GITHUB");
        if (urlGitHub != RegrasAtualizacao.UrlDoInstalador(versao).ToString()) return Registrar(versao, "ERRO", "URL_INESPERADA");

        string pasta;
        try
        {
            pasta = _area.NovaPastaDeInstalacao();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Registrar(versao, "ERRO", "PASTA_DO_SISTEMA");
        }
        var arquivo = Path.Combine(pasta, $"IntegradorIABS-{versao}.msi");
        string shaArquivo;
        try
        {
            shaArquivo = await BaixarAsync(http, RegrasAtualizacao.UrlDoInstalador(versao), arquivo, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            ApagarSemErro(arquivo);
            return Registrar(versao, "ERRO", ex is InvalidOperationException ? ex.Message : "DOWNLOAD_FALHOU");
        }
        if (shaArquivo != sha)
        {
            ApagarSemErro(arquivo);
            return Registrar(versao, "ERRO", "SHA256_DIVERGENTE_ARQUIVO");
        }
        // de novo, logo antes do msiexec: o arquivo que vai ser instalado é o conferido
        if (await ShaDoArquivoAsync(arquivo, ct) != sha)
        {
            ApagarSemErro(arquivo);
            return Registrar(versao, "ERRO", "SHA256_DIVERGENTE_ARQUIVO");
        }

        var log = Path.Combine(pasta, $"instalar-{versao}.log");
        var inicio = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "/i", arquivo, "/qn", "/norestart", "/l*v", log }) inicio.ArgumentList.Add(a);
        Registrar(versao, "INSTALANDO", null);
        try
        {
            var iniciou = iniciarProcesso?.Invoke(inicio) ?? Process.Start(inicio) is not null;
            if (!iniciou) return Registrar(versao, "ERRO", "INSTALADOR_NAO_INICIOU");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return Registrar(versao, "ERRO", "INSTALADOR_NAO_INICIOU");
        }
        logger.LogInformation("Atualização para {Versao}: instalador conferido (SHA-256 da IABS, do GitHub e do arquivo) e iniciado.", versao);
        return new SituacaoAtualizacao(versao, "INSTALANDO", null, DateTimeOffset.Now);
    }

    private HttpClient CriarHttp()
    {
        var cfg = ConfiguracaoIntegrador.Carregar(caminhos);
        HttpMessageHandler h;
        if (handler is not null)
        {
            h = handler;
        }
        else
        {
            var s = Rede.CriarHandler(cfg?.ProxyPrincipal ?? new ConfiguracaoProxy());
            s.AllowAutoRedirect = false; // cada redirecionamento é conferido (só GitHub)
            h = s;
        }
        var http = new HttpClient(h, disposeHandler: handler is null) { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(Constantes.UserAgent);
        return http;
    }

    /// <summary>GET seguindo até 5 redirecionamentos, todos para endereços do GitHub.</summary>
    private static async Task<HttpResponseMessage> PegarAsync(HttpClient http, Uri url, CancellationToken ct)
    {
        for (var i = 0; i < 6; i++)
        {
            if (!RegrasAtualizacao.HostPermitido(url)) throw new InvalidOperationException("ENDERECO_NAO_PERMITIDO");
            var r = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)r.StatusCode is >= 300 and < 400 && r.Headers.Location is { } destino)
            {
                url = destino.IsAbsoluteUri ? destino : new Uri(url, destino);
                r.Dispose();
                continue;
            }
            return r;
        }
        throw new InvalidOperationException("REDIRECIONAMENTOS_DEMAIS");
    }

    /// <summary>Baixa para o arquivo e devolve o SHA-256 (minúsculo).</summary>
    private static async Task<string> BaixarAsync(HttpClient http, Uri url, string arquivo, CancellationToken ct)
    {
        using var r = await PegarAsync(http, url, ct);
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException("DOWNLOAD_FALHOU");
        if (r.Content.Headers.ContentLength is > TamanhoMaximo) throw new InvalidOperationException("ARQUIVO_GRANDE_DEMAIS");
        await using var origem = await r.Content.ReadAsStreamAsync(ct);
        await using var destino = new FileStream(arquivo, FileMode.Create, FileAccess.Write, FileShare.None);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        int lidos;
        while ((lidos = await origem.ReadAsync(buffer, ct)) > 0)
        {
            total += lidos;
            if (total > TamanhoMaximo) throw new InvalidOperationException("ARQUIVO_GRANDE_DEMAIS");
            sha.AppendData(buffer, 0, lidos);
            await destino.WriteAsync(buffer.AsMemory(0, lidos), ct);
        }
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    private static async Task<string> ShaDoArquivoAsync(string arquivo, CancellationToken ct)
    {
        await using var f = new FileStream(arquivo, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(f, ct));
    }

    private static void ApagarSemErro(string arquivo)
    {
        try
        {
            File.Delete(arquivo);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private SituacaoAtualizacao Registrar(string versao, string situacao, string? codigo)
    {
        var s = new SituacaoAtualizacao(versao, situacao, codigo, DateTimeOffset.Now);
        try
        {
            _area.Guarda.Gravar(s);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // sem a situação, a IABS só não vê o motivo
        }
        if (situacao == "ERRO") logger.LogWarning("Atualização para {Versao} não feita: {Codigo}.", versao, codigo);
        return s;
    }
}
