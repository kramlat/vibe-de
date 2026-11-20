# PAMSAM: HKEY_LOCAL_MACHINE\SAM

`pamsam` (src/PAMSAM, logic in src/Vde.PamSam) is a small root service that makes the system's user accounts
visible in the registry, as the key `SAM` of HKLM, the way Windows' security accounts manager does. The accounts
are the Linux account model PAM works from (`/etc/passwd`, `/etc/group` and the facts of `/etc/shadow`); PAMSAM
never manages accounts, it only shows them. Add, change and remove users the usual way (useradd, passwd, ...);
the key follows within a moment.

```
systemd ─▶ pamsam (root, pamsam.socket) ──fhive protocol, /run/pamsam/sam.sock──▶ clients
                  └─ builds the hive from passwd/group/shadow, rebuilds when they change
clients: FhiveClient.OpenStore() mounts the socket as HKLM\SAM (MountedHive) when the service is running
```

## Layout

```
SAM\Domains\Account                       Name (this machine), UserCount, AdministratorGroups
SAM\Domains\Account\Users\<name>          Uid, Gid (Int64), FullName, Home, Shell, Groups (MultiString), Administrator (Bool)
                                          PasswordState (Set | Locked | None), PasswordLastSet, PasswordExpires,
                                          AccountExpires (DateTime): only the account itself and root can read these
SAM\Domains\Account\Aliases\<group>       Gid, Members (MultiString)
```

`Administrator` is membership of `wheel`, `sudo` or `admin`. The password hash is never copied into the registry.

## Who sees what

The service serves the hive with the same protocol and the same rules as `fhived`: it asks the kernel who each
client is (`SO_PEERCRED`) and applies the permissions in the hive to that uid. Names, ids, homes, shells and groups
are public, as in `/etc/passwd`. The password facts are mode 0400 owned by the account, so each user sees their own
and root sees all. Nothing can be changed through it; whatever root writes in memory is gone at the next refill.

## Notes

- Without PAMSAM running (or without root, for the shadow facts) nothing breaks: there is just no `SAM` key, or no
  password facts.
- Elevation (the UAC-style prompt through sudo) and checking a password (the lock screen) are separate pieces that
  will use the same account model; they are not part of this service yet.
- Accounts from other directory services (LDAP, systemd-homed) are not listed: only the local files.
