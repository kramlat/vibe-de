# DOS devices (dosdevman)

Drive letters, the way Windows users know them, and a handle on Wine that makes the main bottle easy to manage.

`dosdevman` is a **systemd user service**. It keeps one map of drive letters for the user, and makes it real in the places that read it:
File Explorer (This Computer shows `Data (D:)`), and Wine (`<prefix>/dosdevices/`). Wine's own `dosdevices` convention is the target:
`d:` is a link to the folder, `d::` a link to the device (so Wine knows the drive type).

## The map

Kept in the user's registry, `HKCU\Software\Vde\DosDevices\Drives\<letter>`, so **every user has their own letters and their own C:**.
There is no shared, system-wide C:. A letter is one of:

| Kind | What it is |
|---|---|
| Device | A block device, named by its filesystem UUID (`/dev/disk/by-uuid`), or PARTUUID, so it keeps its letter when it is plugged into another port. Asked for, never automatic: nothing gets a letter until you say so. |
| Folder | A folder mounted "in reverse" as a drive, like `subst`. |
| WineRoot | A Wine prefix: its `drive_c` is the drive. The **main bottle** is the prefix whose `drive_c` is **C:**. |

C: is always the main bottle, Z: is always `/`, and the next free letter for a device is D: and up.

## The tool

```
dosdevman list
dosdevman assign <device|mount point|label> [letter]   give a mounted drive a letter
dosdevman mount <folder> [letter] [--label NAME]       a folder as a drive letter
dosdevman bottle [prefix]                              show, or set, the main bottle (C:)
dosdevman bottles add|remove <prefix>                  other Wine prefixes kept in step
dosdevman unassign <letter>
dosdevman sync                                         write the links now
dosdevman daemon                                       keep them in step (the service)
```

File Explorer does the same from This Computer: right-click a drive for **Assign a drive letter** or **Remove drive letter**.

## The service

`data/systemd/dosdevman.service` (a user unit, `WantedBy=default.target`, after the user's `fhived.socket`):

```
systemctl --user enable --now dosdevman
```

`start-vde` also starts it with the session. The daemon writes the links at start and again whenever the map changes in the registry or a drive
is mounted or unmounted. It writes into `~/.local/share/vde/dosdevices` and into the `dosdevices` folder of every bottle that is kept in step.

**What it will and will not touch.** Only symbolic links, and only ones it wrote itself (it keeps a `.dosdevman` list of them in each folder).
A real file or folder in the way is left alone, and in a Wine prefix its own `c:` and `z:` are never changed.

## For the custom Wine

The map is plain data in the user's registry and plain links on disk, so a Wine build can read the letters straight from
`HKCU\Software\Vde\DosDevices` instead of guessing at mounts.
