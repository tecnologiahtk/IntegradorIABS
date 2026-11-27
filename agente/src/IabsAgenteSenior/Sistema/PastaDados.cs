using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace IabsAgenteSenior.Sistema;

/// <summary>Permissões e remoção da pasta de dados (C:\ProgramData\IABS\AgenteSenior).</summary>
public static class PastaDados
{
    private static readonly SecurityIdentifier Sistema = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administradores = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier Usuarios = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier TrustedInstaller = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");
    private const InheritanceFlags Heranca = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    /// <summary>
    /// Deixa a pasta só para SYSTEM, administradores, a conta do serviço e quem
    /// o instalador liberou (configuradores: quem instalou abre a janela sem o
    /// controle de conta de usuário), sem herdar as permissões de C:\ProgramData.
    /// Como administrador (o instalador roda como SYSTEM; revisão de 27/11/2026):
    /// o dono passa a ser os administradores (o dono sempre pode mudar as
    /// permissões), a pasta que já existia com dono de fora ou como junção é
    /// afastada e criada de novo (alguém a criou antes da instalação para ficar
    /// dono e ler a senha do banco) e C:\ProgramData\IABS fica só para SYSTEM e
    /// administradores gravarem. Devolve a mensagem para a tela.
    /// </summary>
    public static string Proteger(string pasta, IEnumerable<SecurityIdentifier>? configuradores = null)
    {
        var liberados = (configuradores ?? []).ToList();
        var comoAdministrador = EhAdministrador();
        string? afastada = null;
        if (comoAdministrador)
        {
            ProtegerMae(pasta);
            afastada = AfastarSeForDeOutro(pasta, liberados);
        }

        var info = Directory.CreateDirectory(pasta);
        var seguranca = new DirectorySecurity();
        seguranca.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        seguranca.AddAccessRule(new FileSystemAccessRule(Sistema, FileSystemRights.FullControl, Heranca, PropagationFlags.None, AccessControlType.Allow));
        seguranca.AddAccessRule(new FileSystemAccessRule(Administradores, FileSystemRights.FullControl, Heranca, PropagationFlags.None, AccessControlType.Allow));
        if (comoAdministrador) seguranca.SetOwner(Administradores);
        foreach (var sid in liberados)
        {
            seguranca.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.Modify, Heranca, PropagationFlags.None, AccessControlType.Allow));
        }

        string mensagem;
        try
        {
            var servico = (SecurityIdentifier)new NTAccount(Constantes.ContaServico).Translate(typeof(SecurityIdentifier));
            seguranca.AddAccessRule(new FileSystemAccessRule(servico, FileSystemRights.Modify, Heranca, PropagationFlags.None, AccessControlType.Allow));
            mensagem = $"Pasta de dados protegida: SYSTEM, administradores, {Constantes.ContaServico}"
                       + (liberados.Count > 0 ? $" e {liberados.Count} usuário(s) liberado(s) na instalação." : ".");
        }
        catch (IdentityNotMappedException)
        {
            mensagem = $"A conta {Constantes.ContaServico} ainda não existe (serviço não instalado). A pasta ficou só para SYSTEM e administradores.";
        }
        info.SetAccessControl(seguranca);
        return afastada is null ? mensagem : $"{afastada} {mensagem}";
    }

    /// <summary>
    /// A pasta do programa (2.1.4): a TI pode escolher uma fora de Program Files,
    /// que herdaria as permissões do disco (em C:\, qualquer usuário altera) e
    /// deixaria trocar o executável que roda como serviço. Fica só para SYSTEM e
    /// administradores alterarem, com dono administradores; os usuários leem e
    /// executam (o ícone e a janela). Os arquivos herdam (o instalador não põe
    /// permissão própria neles). Só como administrador.
    /// </summary>
    public static string ProtegerPastaDoPrograma(string pasta)
    {
        var info = new DirectoryInfo(pasta);
        if (!info.Exists) return $"AVISO: a pasta do programa {pasta} não existe.";
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException($"A pasta do programa {pasta} é uma junção ou atalho: escolha outra.");
        var seguranca = new DirectorySecurity();
        seguranca.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        seguranca.AddAccessRule(new FileSystemAccessRule(Sistema, FileSystemRights.FullControl, Heranca, PropagationFlags.None, AccessControlType.Allow));
        seguranca.AddAccessRule(new FileSystemAccessRule(Administradores, FileSystemRights.FullControl, Heranca, PropagationFlags.None, AccessControlType.Allow));
        seguranca.AddAccessRule(new FileSystemAccessRule(Usuarios, FileSystemRights.ReadAndExecute, Heranca, PropagationFlags.None, AccessControlType.Allow));
        seguranca.SetOwner(Administradores);
        info.SetAccessControl(seguranca);
        return $"Pasta do programa protegida: {pasta} (SYSTEM e administradores alteram; usuários só leem e executam).";
    }

    /// <summary>Administrador de verdade (token elevado) ou SYSTEM: pode trocar o dono.</summary>
    private static bool EhAdministrador()
    {
        using var eu = WindowsIdentity.GetCurrent();
        return eu.IsSystem || new WindowsPrincipal(eu).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool DonoConfiavel(SecurityIdentifier? dono, IReadOnlyList<SecurityIdentifier> liberados) =>
        dono is not null && (dono == Sistema || dono == Administradores || dono == TrustedInstaller || liberados.Contains(dono));

    /// <summary>
    /// A pasta que já existe com dono de fora (nem SYSTEM, nem administradores,
    /// nem quem o instalador liberou) ou como junção não é usada: a junção sai
    /// (sem tocar no destino) e a pasta vai para "...recusada-AAAAMMDDHHMMSS",
    /// só para administradores. Só a pasta muda de dono, nunca o que está dentro.
    /// Devolve a mensagem, ou null quando a pasta é a de sempre.
    /// </summary>
    private static string? AfastarSeForDeOutro(string pasta, IReadOnlyList<SecurityIdentifier> liberados)
    {
        var dir = new DirectoryInfo(pasta);
        if (!dir.Exists) return null;
        if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            dir.Delete(); // tira a junção, não o destino
            return $"A pasta de dados era uma junção ou atalho: removida e criada de novo.";
        }
        var dono = dir.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (DonoConfiavel(dono, liberados)) return null;

        var posse = new DirectorySecurity();
        posse.SetOwner(Administradores);
        dir.SetAccessControl(posse);
        var fechada = new DirectorySecurity();
        fechada.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        fechada.AddAccessRule(new FileSystemAccessRule(Sistema, FileSystemRights.FullControl, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        fechada.AddAccessRule(new FileSystemAccessRule(Administradores, FileSystemRights.FullControl, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        dir.SetAccessControl(fechada);
        var destino = $"{Path.TrimEndingDirectorySeparator(pasta)}.recusada-{DateTime.Now:yyyyMMddHHmmss}";
        dir.MoveTo(destino);
        return $"A pasta de dados já existia com outro dono ({dono?.Value ?? "?"}): afastada para {destino} e criada de novo.";
    }

    /// <summary>
    /// C:\ProgramData\IABS (a mãe da pasta padrão): dono administradores e só
    /// SYSTEM e administradores gravam; os usuários só listam esta pasta. Assim
    /// ninguém de fora renomeia a pasta de dados para pôr uma junção no lugar.
    /// </summary>
    private static void ProtegerMae(string pasta)
    {
        var mae = Directory.GetParent(Path.TrimEndingDirectorySeparator(pasta));
        if (mae is null || !string.Equals(mae.Name, "IABS", StringComparison.OrdinalIgnoreCase)) return;
        var programData = Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        if (!string.Equals(Path.TrimEndingDirectorySeparator(mae.Parent?.FullName ?? ""), programData, StringComparison.OrdinalIgnoreCase)) return;
        mae.Create();
        mae.Refresh();
        if (mae.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException($"{mae.FullName} é uma junção ou atalho: confira a máquina antes de instalar.");
        var seguranca = new DirectorySecurity();
        seguranca.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        seguranca.AddAccessRule(new FileSystemAccessRule(Sistema, FileSystemRights.FullControl, Heranca, PropagationFlags.None, AccessControlType.Allow));
        seguranca.AddAccessRule(new FileSystemAccessRule(Administradores, FileSystemRights.FullControl, Heranca, PropagationFlags.None, AccessControlType.Allow));
        seguranca.AddAccessRule(new FileSystemAccessRule(Usuarios, FileSystemRights.ReadAndExecute, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        seguranca.SetOwner(Administradores);
        mae.SetAccessControl(seguranca);
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

        // sem entrar em junção ou atalho de pasta: só o que está dentro desta pasta sai
        var semDesvio = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
        var pendentes = 0;
        foreach (var arquivo in Directory.EnumerateFiles(pasta, "*", semDesvio))
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
        foreach (var dir in Directory.EnumerateDirectories(pasta, "*", semDesvio).OrderByDescending(d => d.Length).Append(pasta))
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
