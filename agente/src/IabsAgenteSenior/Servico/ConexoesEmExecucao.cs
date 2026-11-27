using System.Text.Json;
using IabsAgenteSenior.Canal;
using IabsAgenteSenior.Configuracao;
using Microsoft.Extensions.Logging;

namespace IabsAgenteSenior.Servico;

/// <summary>
/// Os canais com a IABS em execução, um por conexão (2.2.0). Sincronizar
/// compara cada conexão do arquivo com a que está rodando: igual segue sem
/// cair; mudou, fecha e abre de novo; nova abre; removida fecha. Assim salvar
/// a homologação não derruba o canal da produção.
/// </summary>
internal sealed class ConexoesEmExecucao(Func<ConfiguracaoLocal, EstadoCanal, CancellationToken, Task> rodar, ILogger logger)
{
    private readonly List<Rodando> _rodando = [];

    private sealed record Rodando(ConfiguracaoLocal Cfg, string Assinatura, CancellationTokenSource Cts, Task Tarefa, EstadoCanal Estado);

    public async Task SincronizarAsync(IReadOnlyList<ConfiguracaoLocal> conexoes)
    {
        var desejadas = conexoes.Select(c => (Cfg: c, Assinatura: Assinatura(c))).ToList();

        foreach (var r in _rodando.ToList())
        {
            var mesma = desejadas.FirstOrDefault(d => ConfiguracaoIntegrador.MesmoNome(d.Cfg.Nome, r.Cfg.Nome));
            if (mesma.Cfg is not null && mesma.Assinatura == r.Assinatura) continue;
            logger.LogInformation(mesma.Cfg is not null
                ? "Conexão {Conexao}: configuração alterada, reabrindo o canal com a IABS."
                : "Conexão {Conexao} removida da configuração: fechando o canal com a IABS.", r.Cfg.Nome);
            await PararAsync(r);
            _rodando.Remove(r);
        }

        // na ordem do arquivo: é a ordem da janela e do ícone
        var ordem = new List<Rodando>();
        foreach (var d in desejadas)
        {
            ordem.Add(_rodando.FirstOrDefault(r => ConfiguracaoIntegrador.MesmoNome(r.Cfg.Nome, d.Cfg.Nome)) ?? Iniciar(d.Cfg, d.Assinatura));
        }
        _rodando.Clear();
        _rodando.AddRange(ordem);
    }

    public IReadOnlyList<(ConfiguracaoLocal Cfg, EstadoCanal.Retrato Estado)> Retratos() =>
        _rodando.Select(r => (r.Cfg, r.Estado.Ler())).ToList();

    public async Task PararTodasAsync()
    {
        foreach (var r in _rodando) await PararAsync(r);
        _rodando.Clear();
    }

    private Rodando Iniciar(ConfiguracaoLocal cfg, string assinatura)
    {
        logger.LogInformation("Conexão {Conexao}: abrindo o canal com a IABS.", cfg.Nome);
        var cts = new CancellationTokenSource();
        var estado = new EstadoCanal();
        var tarefa = Task.Run(() => rodar(cfg, estado, cts.Token));
        return new Rodando(cfg, assinatura, cts, tarefa, estado);
    }

    private async Task PararAsync(Rodando r)
    {
        await r.Cts.CancelAsync();
        try
        {
            await r.Tarefa;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning("Conexão {Conexao}: o canal terminou com erro ao fechar ({Tipo}).", r.Cfg.Nome, ex.GetType().Name);
        }
        r.Cts.Dispose();
        r.Estado.Desconectou("Reabrindo o canal.");
    }

    /// <summary>A conexão como está no arquivo (segredos cifrados): mudou um campo, mudou a assinatura.</summary>
    private static string Assinatura(ConfiguracaoLocal c) => JsonSerializer.Serialize(c, ConfiguracaoLocal.Json);
}
