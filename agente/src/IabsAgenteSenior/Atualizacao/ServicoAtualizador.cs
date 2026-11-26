using IabsAgenteSenior.Configuracao;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IabsAgenteSenior.Atualizacao;

/// <summary>
/// O serviço "Integrador IABS (atualizador)": início manual, como SYSTEM. O
/// integrador o liga quando a IABS pede uma versão nova; ele roda o Atualizador
/// uma vez e se desliga (o msiexec segue sozinho e religa o integrador).
/// </summary>
public sealed class ServicoAtualizador(Caminhos caminhos, ILoggerFactory fabrica, IHostApplicationLifetime vida) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var logger = fabrica.CreateLogger<Atualizador>();
        try
        {
            var r = await new Atualizador(caminhos, logger).ExecutarAsync(stoppingToken);
            logger.LogInformation("Atualizador terminou: {Situacao} {Codigo}.", r.Situacao, r.Codigo);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError("Atualizador falhou ({Tipo}).", ex.GetType().Name);
        }
        finally
        {
            vida.StopApplication();
        }
    }
}
