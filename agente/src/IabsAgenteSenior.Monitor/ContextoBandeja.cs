using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using IabsAgenteSenior.Configuracao;
using IabsAgenteSenior.Servico;

namespace IabsAgenteSenior.Monitor;

/// <summary>
/// Ícone perto do relógio. Atualiza a cada 30 segundos; avisa quando passa a
/// erro; dois cliques abrem a configuração. Só lê: nada aqui altera o agente.
/// </summary>
internal sealed class ContextoBandeja : ApplicationContext
{
    private readonly Caminhos _caminhos;
    private readonly NotifyIcon _icone;
    private readonly ContextMenuStrip _menu = new() { ShowImageMargin = false };
    private readonly System.Windows.Forms.Timer _relogio = new() { Interval = 30_000 };
    private readonly Form _janelaOculta = new() { ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None, Size = new Size(0, 0) };
    private Nivel? _nivelAnterior;
    private Icon? _iconeAtual;

    public ContextoBandeja(Caminhos caminhos)
    {
        _caminhos = caminhos;
        _icone = new NotifyIcon { ContextMenuStrip = _menu, Visible = true };
        _icone.DoubleClick += (_, _) => AbrirConfiguracao();
        _relogio.Tick += (_, _) => Atualizar();
        _relogio.Start();

        // janela invisível: o instalador fecha o ícone mandando WM_CLOSE para ela
        _ = _janelaOculta.Handle;
        _janelaOculta.FormClosed += (_, _) => ExitThread();

        Atualizar();
    }

    private void Atualizar()
    {
        var aparencia = CalculoAparencia.Calcular(StatusPainel.Ler(_caminhos), EstadoServico(), DateTimeOffset.Now);

        var anterior = _iconeAtual;
        _iconeAtual = Desenhar(aparencia.Nivel);
        _icone.Icon = _iconeAtual;
        anterior?.Dispose();
        _icone.Text = aparencia.Dica;
        MontarMenu(aparencia);

        if (aparencia.Nivel == Nivel.Erro && _nivelAnterior is not null && _nivelAnterior != Nivel.Erro)
        {
            _icone.ShowBalloonTip(10_000, "Integrador IABS", $"Atenção: {aparencia.Titulo}.", ToolTipIcon.Warning);
        }
        _nivelAnterior = aparencia.Nivel;
    }

    private static string? EstadoServico()
    {
        try
        {
            using var sc = new ServiceController(Constantes.NomeServico);
            return sc.Status.ToString();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private void MontarMenu(Aparencia a)
    {
        _menu.Items.Clear();
        _menu.Items.Add(new ToolStripMenuItem($"Integrador IABS {Constantes.Versao}") { Enabled = false, Font = new Font(_menu.Font, FontStyle.Bold) });
        _menu.Items.Add(new ToolStripMenuItem($"● {char.ToUpper(a.Titulo[0])}{a.Titulo[1..]}") { Enabled = false, ForeColor = Cor(a.Nivel) });
        foreach (var linha in a.Linhas) _menu.Items.Add(new ToolStripMenuItem(linha) { Enabled = false });
        _menu.Items.Add(new ToolStripSeparator());
        var estado = EstadoServico();
        if (estado == "Running")
        {
            _menu.Items.Add("Parar serviço...", null, (_, _) => ControlarServico(iniciar: false));
        }
        else if (estado == "Stopped")
        {
            _menu.Items.Add("Iniciar serviço...", null, (_, _) => ControlarServico(iniciar: true));
        }
        _menu.Items.Add("Abrir configuração...", null, (_, _) => AbrirConfiguracao());
        _menu.Items.Add("Atualizar agora", null, (_, _) => Atualizar());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Ocultar ícone (o serviço continua rodando)", null, (_, _) => ExitThread());
    }

    /// <summary>
    /// Parar ou iniciar o serviço pede administrador: pelo configurador (UAC), que
    /// faz só isso e sai. Depois, confere o estado a cada 2 segundos por até 40 s.
    /// </summary>
    private void ControlarServico(bool iniciar)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "IabsAgenteSenior.Configurador.exe");
        try
        {
            Process.Start(new ProcessStartInfo(exe, iniciar ? "--iniciar-servico" : "--parar-servico") { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            return; // UAC cancelado
        }
        var esperado = iniciar ? "Running" : "Stopped";
        var tentativas = 0;
        var verificador = new System.Windows.Forms.Timer { Interval = 2_000 };
        verificador.Tick += (_, _) =>
        {
            tentativas++;
            Atualizar();
            if (EstadoServico() == esperado || tentativas >= 20)
            {
                verificador.Stop();
                verificador.Dispose();
            }
        };
        verificador.Start();
    }

    private void AbrirConfiguracao()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "IabsAgenteSenior.Configurador.exe");
        try
        {
            // pede administrador (UAC) pelo manifesto do configurador
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            // UAC cancelado ou configurador ausente: nada a fazer
        }
    }

    private static Color Cor(Nivel nivel) => nivel switch
    {
        Nivel.Ok => Color.FromArgb(30, 150, 70),
        Nivel.Atencao => Color.FromArgb(225, 160, 0),
        _ => Color.FromArgb(200, 40, 30),
    };

    /// <summary>Bolinha colorida com um "I" branco, no tamanho de ícone pequeno do sistema.</summary>
    private static Icon Desenhar(Nivel nivel)
    {
        var tamanho = SystemInformation.SmallIconSize;
        using var bmp = new Bitmap(tamanho.Width, tamanho.Height);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            var area = new Rectangle(0, 0, tamanho.Width - 1, tamanho.Height - 1);
            using var fundo = new SolidBrush(Cor(nivel));
            g.FillEllipse(fundo, area);
            using var fonte = new Font("Segoe UI", tamanho.Height * 0.52f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var formato = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("I", fonte, Brushes.White, new RectangleF(0, 0, tamanho.Width, tamanho.Height + 1), formato);
        }
        var handle = bmp.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _relogio.Dispose();
            _icone.Visible = false;
            _icone.Dispose();
            _iconeAtual?.Dispose();
            _menu.Dispose();
            _janelaOculta.Dispose();
        }
        base.Dispose(disposing);
    }
}
