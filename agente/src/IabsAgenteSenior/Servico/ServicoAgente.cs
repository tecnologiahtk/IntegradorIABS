using System.ServiceProcess;
using IabsAgenteSenior.Atualizacao;
using IabsAgenteSenior.Banco;
using IabsAgenteSenior.Canal;
using IabsAgenteSenior.Configuracao;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IabsAgenteSenior.Servico;

/// <summary>
/// O serviço do Windows: mantém um canal com a IABS por conexão (2.2.0, ex.:
/// Produção e Homologação, cada uma com endereço, chave e banco próprios) e
/// atende os pedidos. Confere a configuração a cada 15 segundos: a conexão
/// que mudou (a TI salvou na janela) fecha o canal e abre de novo, a nova abre
/// e a removida fecha; as outras seguem sem cair (ConexoesEmExecucao).
/// </summary>
public sealed class ServicoAgente(Caminhos caminhos, ILoggerFactory fabrica) : BackgroundService
{
    private static readonly TimeSpan Intervalo = TimeSpan.FromSeconds(15);
    private readonly ILogger _logger = fabrica.CreateLogger<ServicoAgente>();
    private readonly Lock _travaAtualizacao = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Serviço {Nome} {Versao} iniciado. Pasta de dados: {Pasta}", Constantes.NomeExibicao, Constantes.Versao, caminhos.PastaDados);
        var conexoes = new ConexoesEmExecucao(RodarConexaoAsync, _logger);
        var avisouSemConfiguracao = DateTimeOffset.MinValue;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ConfiguracaoIntegrador? cfg = null;
                var legivel = true;
                try
                {
                    cfg = ConfiguracaoIntegrador.Carregar(caminhos);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                {
                    // arquivo ilegível: os canais abertos continuam como estão
                    legivel = false;
                    _logger.LogError("Configuração ilegível ({Tipo}). Abra a configuração e salve de novo.", ex.GetType().Name);
                }

                if (legivel)
                {
                    if ((cfg?.Conexoes.Count ?? 0) == 0 && DateTimeOffset.Now - avisouSemConfiguracao > TimeSpan.FromHours(1))
                    {
                        _logger.LogWarning("Integrador sem configuração. Abra \"Configurar Integrador IABS\" no menu Iniciar (ou rode: IabsAgenteSenior.exe configurar).");
                        avisouSemConfiguracao = DateTimeOffset.Now;
                    }
                    await conexoes.SincronizarAsync(cfg?.Conexoes ?? []);
                }
                Painel(conexoes);
                if (!await EsperarAsync(Intervalo, stoppingToken)) break;
            }
        }
        finally
        {
            await conexoes.PararTodasAsync();
        }
        _logger.LogInformation("Serviço {Nome} parado.", Constantes.NomeExibicao);
    }

    /// <summary>
    /// O canal de uma conexão até ela ser fechada. Erro inesperado no canal
    /// (2.1.4) não para o serviço: o canal abre de novo em 30 s.
    /// </summary>
    private async Task RodarConexaoAsync(ConfiguracaoLocal cfg, EstadoCanal estado, CancellationToken ct)
    {
        // o nome entre colchetes separa no log o que é de cada conexão
        var logCanal = fabrica.CreateLogger($"CanalIabs [{cfg.Nome}]");
        var logBanco = fabrica.CreateLogger($"BancoSenior [{cfg.Nome}]");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var operacoes = new OperacoesSenior(new BancoSenior(cfg.Banco, logBanco));
                var despachante = new Despachante(operacoes, () => cfg.GravacaoLiberada, (p, c) => PedirAtualizacaoAsync(cfg.Nome, p, c));
                var canal = new CanalIabs(cfg, despachante, operacoes.InfoAsync, estado, logCanal,
                    ultimaAtualizacao: () => ArquivosAtualizacao.LerSituacao(caminhos));
                await canal.RodarAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError("Conexão {Conexao}: canal com a IABS parou por um erro inesperado ({Tipo}); abrindo de novo em 30 s.", cfg.Nome, ex.GetType().Name);
                estado.Desconectou("Erro inesperado no canal: abrindo de novo em 30 segundos.");
                if (!await EsperarAsync(TimeSpan.FromSeconds(30), ct)) break;
            }
        }
    }

    /// <summary>
    /// Pedido de atualização da IABS (2.1.0): deixa o pedido na pasta de dados e
    /// liga o atualizador (serviço separado, como SYSTEM; o instalador deu a este
    /// serviço só a permissão de ligá-lo). Quem confere e instala é o atualizador.
    /// Qualquer conexão pode pedir (2.2.0); a atualização vale para todas.
    /// </summary>
    private Task PedirAtualizacaoAsync(string conexao, PedidoAtualizacao pedido, CancellationToken ct)
    {
        try
        {
            // duas conexões pedindo juntas: um pedido por vez, e o atualizador liga uma vez só
            lock (_travaAtualizacao)
            {
                ArquivosAtualizacao.GravarPedido(caminhos, pedido);
                ArquivosAtualizacao.GravarSituacao(caminhos, new SituacaoAtualizacao(pedido.Versao, "PEDIDA", null, DateTimeOffset.Now));
                using var sc = new ServiceController(Constantes.NomeServicoAtualizador);
                if (sc.Status == ServiceControllerStatus.Stopped) sc.Start();
            }
            _logger.LogInformation("Atualização para {Versao} pedida pela IABS (conexão {Conexao}): atualizador ligado.", pedido.Versao, conexao);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Atualização para {Versao} (conexão {Conexao}): não consegui ligar o atualizador ({Tipo}).", pedido.Versao, conexao, ex.GetType().Name);
            throw new ErroOperacao("ATUALIZACAO_FALHOU");
        }
        return Task.CompletedTask;
    }

    private static async Task<bool> EsperarAsync(TimeSpan tempo, CancellationToken ct)
    {
        try
        {
            await Task.Delay(tempo, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Grava o status.json que o ícone e a janela leem: uma linha por conexão.</summary>
    private void Painel(ConexoesEmExecucao conexoes)
    {
        try
        {
            var lista = conexoes.Retratos().Select(x => new StatusConexao(
                x.Cfg.Nome,
                x.Estado.Conectado,
                x.Estado.ConectadoDesde,
                x.Estado.DesconectadoDesde,
                x.Estado.UltimoPedidoEm,
                x.Estado.Pedidos,
                x.Estado.ConexaoIabs,
                x.Cfg.GravacaoLiberada,
                x.Estado.Conectado ? null : x.Estado.Mensagem ?? "Conectando à IABS...")).ToList();
            StatusPainel.Gravar(caminhos, new StatusAgente(
                Constantes.Versao,
                DateTimeOffset.Now,
                lista,
                lista.Count == 0 ? "Aguardando configuração: abra Configurar Integrador IABS." : null));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug("Status do painel não gravado ({Tipo}).", ex.GetType().Name);
        }
    }
}
