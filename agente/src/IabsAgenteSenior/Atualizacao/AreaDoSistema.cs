using System.Globalization;
using System.Security.Principal;
using Microsoft.Win32;

namespace IabsAgenteSenior.Atualizacao;

/// <summary>
/// O que o atualizador (SYSTEM) guarda entre um passo e outro: a situação da
/// atualização e o último pedido já tratado (um pedido, uma tentativa).
/// </summary>
public interface IGuardaDaAtualizacao
{
    SituacaoAtualizacao? Ler();
    void Gravar(SituacaoAtualizacao s);
    DateTimeOffset? PedidoTratado();
    void MarcarPedidoTratado(DateTimeOffset em);
}

/// <summary>
/// A guarda no registro: HKLM\SOFTWARE\IABS\AgenteSenior\Atualizacao. Todos
/// leem (o integrador e a janela mostram a situação); só SYSTEM e
/// administradores gravam, e o registro não tem junção nem atalho que desvie
/// a gravação para outro lugar.
/// </summary>
public sealed class GuardaNoRegistro : IGuardaDaAtualizacao
{
    private const string Chave = @"SOFTWARE\IABS\AgenteSenior\Atualizacao";

    private static RegistryKey Base() => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

    public SituacaoAtualizacao? Ler()
    {
        try
        {
            using var b = Base();
            using var k = b.OpenSubKey(Chave);
            if (k?.GetValue("Versao") is not string versao || k.GetValue("Situacao") is not string situacao) return null;
            var em = DateTimeOffset.TryParse(k.GetValue("Em") as string, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d : DateTimeOffset.MinValue;
            return new SituacaoAtualizacao(versao, situacao, k.GetValue("Codigo") as string, em);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return null;
        }
    }

    public void Gravar(SituacaoAtualizacao s)
    {
        using var b = Base();
        using var k = b.CreateSubKey(Chave);
        k.SetValue("Versao", s.Versao);
        k.SetValue("Situacao", s.Situacao);
        k.SetValue("Codigo", s.Codigo ?? "");
        k.SetValue("Em", s.Em.ToString("O", CultureInfo.InvariantCulture));
    }

    public DateTimeOffset? PedidoTratado()
    {
        try
        {
            using var b = Base();
            using var k = b.OpenSubKey(Chave);
            return DateTimeOffset.TryParse(k?.GetValue("PedidoTratado") as string, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d : null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return null;
        }
    }

    public void MarcarPedidoTratado(DateTimeOffset em)
    {
        using var b = Base();
        using var k = b.CreateSubKey(Chave);
        k.SetValue("PedidoTratado", em.ToString("O", CultureInfo.InvariantCulture));
    }

    /// <summary>Tira a chave (desinstalação).</summary>
    public static void Remover()
    {
        try
        {
            using var b = Base();
            b.DeleteSubKeyTree(Chave, throwOnMissingSubKey: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
        }
    }
}

/// <summary>
/// Onde o atualizador (SYSTEM) grava (revisão de 27/11/2026). Ele nunca grava,
/// cria nem apaga nada na pasta de dados: lá a conta do serviço e quem instalou
/// têm Modify e poderiam trocar o instalador baixado ou pôr uma junção no
/// caminho, para o SYSTEM instalar, gravar ou apagar outra coisa. Fica:
///   * o instalador baixado, o log do msiexec e o log do atualizador na pasta
///     local do SYSTEM (C:\Windows\System32\config\systemprofile\AppData\Local\
///     IABS\AgenteSenior), que só SYSTEM e administradores abrem (herdado do
///     perfil do SYSTEM); cada atualização numa subpasta nova, de nome aleatório;
///   * a situação e o último pedido tratado no registro (GuardaNoRegistro).
/// O pedido (atualizar.json) continua na pasta de dados: o atualizador só o lê,
/// e confere a versão no GitHub e o arquivo antes de usar.
/// </summary>
public sealed class AreaDoSistema(string pasta, IGuardaDaAtualizacao guarda)
{
    /// <summary>A área do SYSTEM (o atualizador roda como SYSTEM; no console, a do administrador que rodou).</summary>
    public static AreaDoSistema Padrao() => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IABS", "AgenteSenior"),
        new GuardaNoRegistro());

    /// <summary>A pasta local do SYSTEM, onde o serviço do atualizador grava (para a janela apontar o log).</summary>
    public static string PastaDoSystem =>
        Path.Combine(Environment.SystemDirectory, "config", "systemprofile", "AppData", "Local", "IABS", "AgenteSenior");

    public string Pasta { get; } = pasta;
    public IGuardaDaAtualizacao Guarda { get; } = guarda;
    public string PastaLogs => Path.Combine(Pasta, "logs");
    public string PastaInstaladores => Path.Combine(Pasta, "atualizacao");

    /// <summary>Roda como SYSTEM (o serviço do atualizador).</summary>
    public static bool EhSistema()
    {
        using var eu = WindowsIdentity.GetCurrent();
        return eu.IsSystem;
    }

    /// <summary>
    /// Subpasta nova, de nome aleatório, para esta atualização; as anteriores
    /// saem (o msiexec delas já terminou: o atualizador só roda de novo depois).
    /// </summary>
    public string NovaPastaDeInstalacao()
    {
        Directory.CreateDirectory(PastaInstaladores);
        foreach (var velha in Directory.GetDirectories(PastaInstaladores))
        {
            try
            {
                Directory.Delete(velha, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // presa: sai na próxima
            }
        }
        var nova = Path.Combine(PastaInstaladores, Guid.NewGuid().ToString("N"));
        if (Directory.Exists(nova)) throw new IOException("PASTA_EXISTENTE");
        Directory.CreateDirectory(nova);
        return nova;
    }
}
