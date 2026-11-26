using IabsAgenteSenior.Banco;
using IabsAgenteSenior.Canal;
using IabsAgenteSenior.Configuracao;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IabsAgenteSenior.Servico;

/// <summary>
/// O serviço do Windows: mantém o canal com a IABS aberto e atende os pedidos.
/// Confere a configuração a cada 15 segundos; se ela mudar (a TI salvou na
/// janela), fecha o canal e abre de novo com a configuração nova.
/// </summary>
public sealed class ServicoAgente(Caminhos caminhos, ILoggerFactory fabrica) : BackgroundService
{
    private static readonly TimeSpan Intervalo = TimeSpan.FromSeconds(15);
    private readonly ILogger _logger = fabrica.CreateLogger<ServicoAgente>();
    private readonly EstadoCanal _estado = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Serviço {Nome} {Versao} iniciado. Pasta de dados: {Pasta}", Constantes.NomeExibicao, Constantes.Versao, caminhos.PastaDados);
        var avisouSemConfiguracao = DateTimeOffset.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            ConfiguracaoLocal? cfg = null;
            DateTime gravadaEm = default;
            try
            {
                cfg = ConfiguracaoLocal.Carregar(caminhos);
                if (cfg is not null) gravadaEm = File.GetLastWriteTimeUtc(caminhos.ArquivoConfig);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                _logger.LogError("Configuração ilegível ({Tipo}). Abra a configuração e salve de novo.", ex.GetType().Name);
            }

            if (cfg is null)
            {
                if (DateTimeOffset.Now - avisouSemConfiguracao > TimeSpan.FromHours(1))
                {
                    _logger.LogWarning("Integrador sem configuração. Abra \"Configurar Integrador IABS\" no menu Iniciar (ou rode: IabsAgenteSenior.exe configurar).");
                    avisouSemConfiguracao = DateTimeOffset.Now;
                }
                Painel(null);
                if (!await EsperarAsync(Intervalo, stoppingToken)) break;
                continue;
            }

            await RodarComAsync(cfg, gravadaEm, stoppingToken);
        }
        _logger.LogInformation("Serviço {Nome} parado.", Constantes.NomeExibicao);
    }

    /// <summary>Roda o canal com esta configuração até o serviço parar ou o arquivo mudar.</summary>
    private async Task RodarComAsync(ConfiguracaoLocal cfg, DateTime gravadaEm, CancellationToken stoppingToken)
    {
        using var rodada = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var banco = new BancoSenior(cfg.Banco, fabrica.CreateLogger<BancoSenior>());
        var operacoes = new OperacoesSenior(banco);
        var canal = new CanalIabs(cfg, new Despachante(operacoes, () => cfg.GravacaoLiberada), operacoes.InfoAsync, _estado, fabrica.CreateLogger<CanalIabs>());
        var tarefa = canal.RodarAsync(rodada.Token);

        while (!tarefa.IsCompleted)
        {
            Painel(cfg);
            if (!await EsperarAsync(Intervalo, stoppingToken)) break;
            if (MudouConfiguracao(gravadaEm))
            {
                _logger.LogInformation("Configuração alterada: reabrindo o canal com a IABS.");
                break;
            }
        }
        await rodada.CancelAsync();
        try
        {
            await tarefa;
        }
        catch (OperationCanceledException)
        {
        }
        _estado.Desconectou("Reabrindo o canal.");
    }

    private bool MudouConfiguracao(DateTime gravadaEm)
    {
        try
        {
            return File.Exists(caminhos.ArquivoConfig) && File.GetLastWriteTimeUtc(caminhos.ArquivoConfig) != gravadaEm;
        }
        catch (IOException)
        {
            return false;
        }
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

    /// <summary>Grava o status.json que o ícone e a janela leem.</summary>
    private void Painel(ConfiguracaoLocal? cfg)
    {
        try
        {
            var r = _estado.Ler();
            StatusPainel.Gravar(caminhos, new StatusAgente(
                Constantes.Versao,
                DateTimeOffset.Now,
                cfg is not null,
                cfg is not null && r.Conectado,
                r.ConectadoDesde,
                r.DesconectadoDesde,
                r.UltimoPedidoEm,
                r.Pedidos,
                r.ConexaoIabs,
                cfg?.GravacaoLiberada ?? false,
                cfg is null ? "Aguardando configuração: abra Configurar Integrador IABS." : r.Conectado ? null : r.Mensagem ?? "Conectando à IABS..."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug("Status do painel não gravado ({Tipo}).", ex.GetType().Name);
        }
    }
}
