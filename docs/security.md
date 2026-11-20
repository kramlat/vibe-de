# SECMAN: HKEY_LOCAL_MACHINE\Security

`secman` (src/SECMAN, logic in src/Vde.SecMan) is a small root service that shows what security the system is
really running as the key `Security` of HKLM, the way PAMSAM shows the accounts as `SAM` (see sam.md). Plugins
read the security modules; SECMAN only reports, it never configures them.

```
systemd ─▶ secman (root, secman.socket) ──fhive protocol, /run/secman/security.sock──▶ clients
                  └─ plugins: PAM, SELinux, AppArmor, LSM → one hive, refilled when files change and every 30 s
clients: FhiveClient.OpenStore() mounts the socket as HKLM\Security (MountedHive) when the service is running
```

## Layout

```
Security\Providers\<plugin>      Available (Bool), Error (String, if the plugin failed)
Security\PAM                     ConfigDirectory, ServiceCount, Modules (every module any service uses)
Security\PAM\Services\<service>  auth / account / password / session: MultiString of "control module args", in stack order
Security\SELinux                 Mode (Enforcing | Permissive | Disabled), Enforcing, PolicyVersion, ConfiguredMode, PolicyType
Security\AppArmor                Enabled, ProfileCount, EnforcedCount, ComplainingCount
Security\AppArmor\Profiles\<p>   Mode (enforce | complain): root only (the kernel only tells root)
Security\LSM                     Active (MultiString), Lockdown, YamaPtraceScope, UserNamespaces
```

A plugin appears only when its module exists on the system. A plugin that fails is reported under `Providers` and
does not stop the others. Everything is public except what the kernel itself keeps from ordinary users.

## Adding a plugin

Implement `ISecurityPlugin` (name, availability, `Fill` into its own key, files worth watching) and add it to
`SecurityBuilder.DefaultPlugins`. Plugins read files below the `root` they are given, so they are tested against a
small fake tree. Candidates: firewall status, Secure Boot, audit, polkit rules, sudoers.

## Who sees what

Same as `fhived` and PAMSAM: the service asks the kernel who each client is and applies the permissions in the hive.
Nothing can be changed through it, and anything root writes in memory is gone at the next refill.
