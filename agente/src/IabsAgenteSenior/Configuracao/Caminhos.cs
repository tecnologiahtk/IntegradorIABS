namespace IabsAgenteSenior.Configuracao;

/// <summary>
/// Pastas do integrador. Padrão: C:\ProgramData\IABS\AgenteSenior (configuração,
/// situação e logs). Em desenvolvimento, --dados ou a variável IABS_AGENTE_DADOS
/// apontam para outra pasta.
/// </summary>
public sealed class Caminhos
{
    private Caminhos(string pastaDados, bool padrao)
    {
        PastaDados = pastaDados;
        EhPastaPadrao = padrao;
    }

    public string PastaDados { get; }
    public bool EhPastaPadrao { get; }
    public string ArquivoConfig => Path.Combine(PastaDados, "config.json");
    public string PastaLogs => Path.Combine(PastaDados, "logs");

    public static string PastaPadrao =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "IABS", "AgenteSenior");

    public static Caminhos Resolver(string? pastaInformada)
    {
        var pasta = pastaInformada ?? Environment.GetEnvironmentVariable("IABS_AGENTE_DADOS");
        return pasta is null
            ? new Caminhos(PastaPadrao, true)
            : new Caminhos(Path.GetFullPath(pasta), false);
    }
}
