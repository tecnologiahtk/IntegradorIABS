using System.Security.AccessControl;
using System.Security.Principal;
using IabsAgenteSenior.Sistema;

namespace IabsAgenteSenior.Testes;

/// <summary>
/// A pasta de dados fica só para SYSTEM, administradores, o serviço e quem o
/// instalador liberou (26/11/2026: quem instalou abre a janela sem o controle
/// de conta de usuário). O grupo Usuários nunca entra: a pasta tem a senha do
/// banco, e a cifra é de máquina.
/// </summary>
public sealed class PastaDadosTestes : IDisposable
{
    private readonly string _pasta = Path.Combine(Path.GetTempPath(), $"iabs-pasta-teste-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_pasta)) Directory.Delete(_pasta, recursive: true);
    }

    /// <summary>2.2.1: arquivo sem nenhuma permissão herdada (o estrago do afastamento que falhava) é achado.</summary>
    [Fact]
    public void Arquivo_sem_heranca_e_achado()
    {
        Directory.CreateDirectory(_pasta);
        var arquivo = Path.Combine(_pasta, "config.json");
        File.WriteAllText(arquivo, "{}");
        Assert.False(PastaDados.SemHeranca(arquivo, ehPasta: false));

        // o dono tira a herança e não deixa permissão nenhuma, como ficou o config.json
        var info = new FileInfo(arquivo);
        var fechada = new System.Security.AccessControl.FileSecurity();
        fechada.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        info.SetAccessControl(fechada);
        Assert.True(PastaDados.SemHeranca(arquivo, ehPasta: false));

        // devolve a herança para o Dispose apagar
        var heranca = new System.Security.AccessControl.FileSecurity();
        heranca.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
        info.SetAccessControl(heranca);
        Assert.False(PastaDados.SemHeranca(arquivo, ehPasta: false));
    }

    /// <summary>Sem administrador, o conserto só avisa e não para nada.</summary>
    [Fact]
    public void Conserto_sem_privilegio_avisa_e_nao_falha()
    {
        Directory.CreateDirectory(_pasta);
        File.WriteAllText(Path.Combine(_pasta, "config.json"), "{}");
        var mensagens = PastaDados.RepararHeranca(_pasta);
        Assert.True(mensagens.Count == 0 || mensagens.All(m => m.StartsWith("AVISO", StringComparison.Ordinal) || m.StartsWith("Permissões", StringComparison.Ordinal)));
    }

    /// <summary>2.2.1: o que o atualizador da 2.1.3 deixou na pasta de dados sai na instalação; o resto fica.</summary>
    [Fact]
    public void Restos_da_atualizacao_antiga_saem_e_o_resto_fica()
    {
        Directory.CreateDirectory(Path.Combine(_pasta, "atualizacao"));
        File.WriteAllText(Path.Combine(_pasta, "atualizacao", "IntegradorIABS-2.1.3.msi"), "msi antigo");
        File.WriteAllText(Path.Combine(_pasta, "atualizacao.json"), "{}");
        File.WriteAllText(Path.Combine(_pasta, "config.json"), "{}");
        File.WriteAllText(Path.Combine(_pasta, "atualizar.json"), "{}");

        var mensagens = PastaDados.LimparRestosDaAtualizacaoAntiga(_pasta);

        Assert.Equal(2, mensagens.Count);
        Assert.False(File.Exists(Path.Combine(_pasta, "atualizacao.json")));
        Assert.False(Directory.Exists(Path.Combine(_pasta, "atualizacao")));
        Assert.True(File.Exists(Path.Combine(_pasta, "config.json")));
        Assert.True(File.Exists(Path.Combine(_pasta, "atualizar.json")));
        // sem restos: nada a dizer
        Assert.Empty(PastaDados.LimparRestosDaAtualizacaoAntiga(_pasta));
    }

    [Fact]
    public void Protege_e_mantem_quem_foi_liberado()
    {
        var eu = WindowsIdentity.GetCurrent().User!;
        PastaDados.Proteger(_pasta, [eu]);

        var acl = new DirectoryInfo(_pasta).GetAccessControl();
        Assert.True(acl.AreAccessRulesProtected);
        var regras = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        Assert.Contains(regras, r => r.IdentityReference.Equals(eu) && r.FileSystemRights.HasFlag(FileSystemRights.Modify));
        Assert.Contains(regras, r => r.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)));
        Assert.DoesNotContain(regras, r => r.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null)));
        Assert.DoesNotContain(regras, r => r.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null)));

        // quem foi liberado grava e lê sem ser administrador
        File.WriteAllText(Path.Combine(_pasta, "config.json"), "{}");
        Assert.Equal("{}", File.ReadAllText(Path.Combine(_pasta, "config.json")));
    }
}
