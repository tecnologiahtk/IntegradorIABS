using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace IabsAgenteSenior.Sistema;

/// <summary>Permissões e remoção da pasta de dados (C:\ProgramData\IABS\AgenteSenior).</summary>
public static class PastaDados
{
    /// <summary>
    /// Deixa a pasta só para SYSTEM, administradores, a conta do serviço e quem
    /// o instalador liberou (configuradores: quem instalou abre a janela sem o
    /// controle de conta de usuário), sem herdar as permissões de C:\ProgramData.
    /// Devolve a mensagem para a tela.
    /// </summary>
    public static string Proteger(string pasta, IEnumerable<SecurityIdentifier>? configuradores = null)
    {
        var info = Directory.CreateDirectory(pasta);
        var seguranca = new DirectorySecurity();
        seguranca.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags heranca = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        seguranca.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, heranca, PropagationFlags.None, AccessControlType.Allow));
        seguranca.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, heranca, PropagationFlags.None, AccessControlType.Allow));

        var liberados = (configuradores ?? []).ToList();
        foreach (var sid in liberados)
        {
            seguranca.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.Modify, heranca, PropagationFlags.None, AccessControlType.Allow));
        }

        string mensagem;
        try
        {
            var servico = (SecurityIdentifier)new NTAccount(Constantes.ContaServico).Translate(typeof(SecurityIdentifier));
            seguranca.AddAccessRule(new FileSystemAccessRule(servico, FileSystemRights.Modify, heranca, PropagationFlags.None, AccessControlType.Allow));
            mensagem = $"Pasta de dados protegida: SYSTEM, administradores, {Constantes.ContaServico}"
                       + (liberados.Count > 0 ? $" e {liberados.Count} usuário(s) liberado(s) na instalação." : ".");
        }
        catch (IdentityNotMappedException)
        {
            mensagem = $"A conta {Constantes.ContaServico} ainda não existe (serviço não instalado). A pasta ficou só para SYSTEM e administradores.";
        }
        info.SetAccessControl(seguranca);
        return mensagem;
    }

    /// <summary>
    /// Apaga a pasta inteira. O que estiver preso (antivírus lendo) fica marcado
    /// para apagar na próxima reinicialização. Devolve quantos itens ficaram.
    /// </summary>
    public static int Remover(string pasta)
    {
        var pendentes = RemoverPasta(pasta);
        RemoverMaeSeVazia(pasta);
        return pendentes;
    }

    /// <summary>C:\ProgramData\IABS sai junto quando não sobra nada de outro produto da IABS.</summary>
    private static void RemoverMaeSeVazia(string pasta)
    {
        var mae = Directory.GetParent(Path.TrimEndingDirectorySeparator(pasta));
        if (mae is null || !mae.Exists || !string.Equals(mae.Name, "IABS", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            if (!mae.EnumerateFileSystemInfos().Any()) mae.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // fica para depois: não trava a desinstalação
        }
    }

    private static int RemoverPasta(string pasta)
    {
        if (!Directory.Exists(pasta)) return 0;
        for (var tentativa = 0; tentativa < 5; tentativa++)
        {
            try
            {
                Directory.Delete(pasta, recursive: true);
                return 0;
            }
            catch (IOException)
            {
                Thread.Sleep(1000);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(1000);
            }
        }

        var pendentes = 0;
        foreach (var arquivo in Directory.EnumerateFiles(pasta, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.Delete(arquivo);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MoveFileEx(arquivo, null, MoverAoReiniciar);
                pendentes++;
            }
        }
        foreach (var dir in Directory.EnumerateDirectories(pasta, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length).Append(pasta))
        {
            try
            {
                Directory.Delete(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MoveFileEx(dir, null, MoverAoReiniciar);
            }
        }
        return pendentes;
    }

    private const int MoverAoReiniciar = 0x4; // MOVEFILE_DELAY_UNTIL_REBOOT

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existente, string? novo, int flags);
}
