using IabsAgenteSenior.Configuracao;
using IabsAgenteSenior.Diagnostico;
using Microsoft.Extensions.Logging;

namespace IabsAgenteSenior.Configurador;

internal static class Program
{
    /// <summary>
    /// Janela de configuração do agente. Em desenvolvimento, --dados aponta para
    /// outra pasta de dados (igual à linha de comando).
    /// </summary>
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // chamado pelo ícone perto do relógio: só para ou inicia o serviço (já como
        // administrador, pelo manifesto deste programa) e sai, sem abrir a janela
        if (args.Contains("--iniciar-servico") || args.Contains("--parar-servico"))
        {
            ControlarServico(iniciar: args.Contains("--iniciar-servico"));
            return;
        }

        var i = Array.IndexOf(args, "--dados");
        var pasta = i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        var caminhos = Caminhos.Resolver(pasta);

        // erro inesperado vira mensagem amigável e linha no log, sem a caixa de
        // "exceção sem tratamento" do Windows; a janela continua aberta
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => MostrarErro(caminhos, e.Exception);

        IniciarIcone();
        Application.Run(new JanelaPrincipal(caminhos));
    }

    private static void ControlarServico(bool iniciar)
    {
        try
        {
            using var sc = new System.ServiceProcess.ServiceController(Constantes.NomeServico);
            if (iniciar && sc.Status == System.ServiceProcess.ServiceControllerStatus.Stopped)
            {
                sc.Start();
                sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            }
            else if (!iniciar && sc.Status == System.ServiceProcess.ServiceControllerStatus.Running)
            {
                sc.Stop();
                sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show($"Não consegui {(iniciar ? "iniciar" : "parar")} o serviço Integrador IABS:\n\n{ex.Message}",
                "Integrador IABS", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// Abre o ícone perto do relógio, se ainda não estiver aberto nesta sessão.
    /// Pelo explorer, para o ícone rodar com o usuário comum e não herdar o
    /// administrador desta janela.
    /// </summary>
    private static void IniciarIcone()
    {
        try
        {
            var exe = Path.Combine(AppContext.BaseDirectory, "IabsAgenteSenior.Monitor.exe");
            if (!File.Exists(exe)) return;
            var sessao = System.Diagnostics.Process.GetCurrentProcess().SessionId;
            if (System.Diagnostics.Process.GetProcessesByName("IabsAgenteSenior.Monitor").Any(p => p.SessionId == sessao)) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{exe}\"") { UseShellExecute = false });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // sem ícone: a janela segue normalmente
        }
    }

    private static void MostrarErro(Caminhos caminhos, Exception ex)
    {
        try
        {
            LoggerFactory.Create(b => b.AddProvider(new ArquivoLoggerProvider(caminhos.PastaLogs)))
                .CreateLogger("Configurador").LogError(ex, "Erro inesperado na janela de configuração.");
        }
        catch (Exception)
        {
            // sem log, segue para a mensagem
        }
        MessageBox.Show(
            $"Ocorreu um erro inesperado:\n\n{ArquivoLoggerProvider.Mascarar(ex.Message)}\n\n" +
            $"O detalhe ficou no log em {caminhos.PastaLogs}. Se o problema continuar, gere o diagnóstico na aba Situação e envie ao suporte da IABS.",
            "Configurar Integrador IABS", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
