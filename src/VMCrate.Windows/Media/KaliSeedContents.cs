using System.Text;

namespace VMCrate.Windows;

public static partial class KaliSetupSeed
{
    private const string BootCommand = """
        set timeout=0
        search --no-floppy --file --set=seed /vmcrate.img
        search --no-floppy --file --set=kali /install.amd/vmlinuz
        linux ($kali)/install.amd/vmlinuz preseed/file=/cdrom/simple-cdd/default.preseed simple-cdd/profiles=kali,offline desktop=xfce auto=true priority=critical --- quiet
        initrd ($kali)/install.amd/initrd.gz ($seed)/vmcrate.img
        boot
        """;

    private const string Preseed = """
        d-i preseed/early_command string anna-install eatmydata-udeb
        d-i mirror/country string enter information manually
        d-i mirror/http/hostname string http.kali.org
        d-i mirror/http/directory string /kali
        d-i apt-setup/use_mirror boolean false
        d-i apt-setup/services-select multiselect
        d-i apt-setup/contrib boolean true
        d-i apt-setup/non-free boolean true
        d-i apt-setup/non-free-firmware boolean true
        d-i apt-setup/disable-cdrom-entries boolean true
        d-i apt-setup/enable-source-repositories boolean false
        d-i pkgsel/upgrade select none
        d-i pkgsel/update-policy select none
        popularity-contest popularity-contest/participate boolean false
        d-i debian-installer/language string en
        d-i debian-installer/country string US
        d-i debian-installer/locale string en_US.UTF-8
        d-i keyboard-configuration/xkb-keymap select us
        d-i netcfg/enable boolean false
        d-i netcfg/get_hostname string kali
        d-i netcfg/get_domain string
        d-i hw-detect/load_firmware boolean false
        d-i clock-setup/utc boolean true
        d-i time/zone string Etc/UTC
        d-i clock-setup/ntp boolean false
        d-i passwd/root-login boolean false
        d-i passwd/make-user boolean true
        d-i passwd/user-fullname string __USER__
        d-i passwd/username string __USER__
        d-i passwd/user-password-crypted password __HASH__
        d-i partman-auto/method string regular
        d-i partman-auto/choose_recipe select atomic
        d-i partman-partitioning/choose_label select gpt
        d-i partman-partitioning/default_label string gpt
        d-i partman-partitioning/confirm_write_new_label boolean true
        d-i partman/choose_partition select finish
        d-i partman/confirm boolean true
        d-i partman/confirm_nooverwrite boolean true
        tasksel tasksel/first multiselect standard, core, desktop-xfce
        d-i grub-installer/only_debian boolean true
        d-i grub-installer/with_other_os boolean false
        d-i grub-installer/force-efi-extra-removable boolean true
        d-i cdrom-detect/eject boolean false
        d-i finish-install/reboot_in_progress note
        d-i debian-installer/exit/poweroff boolean true
        """;

    internal static byte[] CreateInitramfs(string user, string passwordHash)
    {
        var text = Preseed.Replace("__USER__", user, StringComparison.Ordinal)
            .Replace("__HASH__", passwordHash, StringComparison.Ordinal).ReplaceLineEndings("\n") + "\n";
        using var output = new MemoryStream();
        WriteEntry(output, "preseed.cfg", 1, 0x81a4, Encoding.UTF8.GetBytes(text));
        WriteEntry(output, "TRAILER!!!", 0, 0, []);
        return output.ToArray();
    }

    private static void WriteEntry(Stream output, string name, int id, int mode, byte[] content)
    {
        var nameBytes = Encoding.ASCII.GetBytes(name + '\0');
        int[] fields = [id, mode, 0, 0, 1, 0, content.Length, 0, 0, 0, 0, nameBytes.Length, 0];
        output.Write(Encoding.ASCII.GetBytes("070701" + string.Concat(fields.Select(value =>
            value.ToString("x8", System.Globalization.CultureInfo.InvariantCulture)))));
        output.Write(nameBytes);
        Align();
        output.Write(content);
        Align();

        void Align()
        {
            while (output.Position % 4 != 0)
                output.WriteByte(0);
        }
    }
}
