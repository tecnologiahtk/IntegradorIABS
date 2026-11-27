using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using IabsAgenteSenior.Configuracao;
using IabsAgenteSenior.Servico;

namespace IabsAgenteSenior.Diagnostico;

/// <summary>
/// Pacote para o suporte da IABS, gerado pela TI do cliente: logs (já
/// mascarados), configuração sem segredos e a situação do canal. Nenhum dado
/// de colaborador.
/// </summary>
public static class PacoteDiagnostico
{
    public static string Gerar(Caminhos caminhos)
    {
        Directory.CreateDirectory(caminhos.PastaDados);
        var destino = Path.Combine(caminhos.PastaDados, $"diagnostico-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        using var zip = ZipFile.Open(destino, ZipArchiveMode.Create);

        var resumo = new Dictionary<string, object?>
        {
            ["versao"] = Constantes.Versao,
            ["protocolo"] = Constantes.Protocolo,
            ["maquina"] = Environment.MachineName,
            ["sistema"] = RuntimeInformation.OSDescription,
            ["gerado_em"] = DateTimeOffset.Now,
            ["pasta_dados"] = caminhos.PastaDados,
            ["conexoes"] = ConfiguracaoIntegrador.Carregar(caminhos)?.SemSegredos(),
            ["situacao"] = StatusPainel.Ler(caminhos),
        };
        var entrada = zip.CreateEntry("resumo.json");
        using (var s = entrada.Open())
        {
            JsonSerializer.Serialize(s, resumo, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        }

        if (Directory.Exists(caminhos.PastaLogs))
        {
            foreach (var log in Directory.GetFiles(caminhos.PastaLogs, "agente-*.log").OrderDescending().Take(14))
            {
                // cópia, porque o serviço pode estar escrevendo no arquivo do dia
                using var origem = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var alvo = zip.CreateEntry($"logs/{Path.GetFileName(log)}").Open();
                origem.CopyTo(alvo);
            }
        }
        return destino;
    }
}
