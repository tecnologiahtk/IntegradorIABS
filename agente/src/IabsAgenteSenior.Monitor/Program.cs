using IabsAgenteSenior.Configuracao;

namespace IabsAgenteSenior.Monitor;

internal static class Program
{
    /// <summary>
    /// Ícone do Integrador IABS perto do relógio. Um por sessão do Windows
    /// (cada usuário logado, inclusive por área de trabalho remota, vê o seu).
    /// </summary>
    [STAThread]
    private static void Main(string[] args)
    {
        using var unico = new Mutex(initiallyOwned: true, @"Local\IabsAgenteSenior.Monitor", out var primeiro);
        if (!primeiro) return;

        ApplicationConfiguration.Initialize();
        var i = Array.IndexOf(args, "--dados");
        var caminhos = Caminhos.Resolver(i >= 0 && i + 1 < args.Length ? args[i + 1] : null);
        Application.Run(new ContextoBandeja(caminhos));
    }
}
