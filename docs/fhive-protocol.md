# fhived socket protocol, version 1

`fhived` serves one hive on a Unix stream socket. A client library (`RemoteHive` in .NET, or a small C
client such as the one a Wine fork would use) speaks this protocol. All integers are little-endian.

## Who the client is

The daemon never trusts the client to say who it is. It asks the kernel for the peer's credentials
(`SO_PEERCRED` for pid/uid/gid, `SO_PEERGROUPS` for the supplementary groups; `/proc/<pid>/status` on kernels
older than 4.13) and applies the hive's permissions to that identity on every request. Root bypasses
permission checks, as everywhere in the registry.

## Framing

```
frame     u32 length            number of bytes that follow (5 .. 8 MiB)
          u8  opcode
          u32 requestId         chosen by the client; replies echo it; events use 0
          ... body
string    u32 byte length, UTF-8 bytes
perms     u32 uid, u32 gid, u16 mode        (mode is 9 bits: rwxrwxrwx)
value     string name, u16 kind, perms, u32 length, data     (data as in the hive file format)
```

A connection starts with `Hello`. Anything else first, an unknown opcode, a malformed body or a frame outside
the length limits ends the connection; other clients are not affected.

## Requests (client to server)

| opcode | name | body | reply body |
|---|---|---|---|
| 0x01 | Hello | u16 version (1) | u16 version, string hive name, u32 uid, u32 gid (as the daemon sees you), u8 readOnly |
| 0x10 | KeyExists | string path | u8 |
| 0x11 | CreateKey | string path | |
| 0x12 | DeleteKey | string path | u8 deleted |
| 0x13 | GetSubKeyNames | string path | u32 count, strings |
| 0x14 | GetKeyInfo | string path | u32 flags, i64 createdTicks, i64 modifiedTicks, i32 subKeys, i32 values, perms |
| 0x15 | SetKeyFlags | string path, u32 flags | |
| 0x16 | GetKeyPermissions | string path | perms |
| 0x17 | SetKeyPermissions | string path, perms | |
| 0x20 | GetValue | string path, string name | u8 present, value |
| 0x21 | GetValues | string path | u32 count, values |
| 0x22 | GetValuePermissions | string path, string name | u8 present, perms |
| 0x23 | SetValue | string path, string name, u16 kind, u32 length + data | |
| 0x24 | SetValuePermissions | string path, string name, perms | |
| 0x25 | DeleteValue | string path, string name | u8 deleted |
| 0x30 | Subscribe | string path | |
| 0x31 | Unsubscribe | | |

Paths use `\`, are case-insensitive, and the empty path is the root key. A value's owner is the caller when it
is new; replacing a value keeps its owner and mode (`SetValue` carries no permissions).

## Replies and events (server to client)

```
0xF0 Reply   requestId = the request's id; body: u8 status, then the reply body above, or string message
0xF1 Event   requestId = 0; body: u8 kind, string keyPath, u8 hasValueName, string valueName
```

Status: 0 ok, 1 not found (KeyNotFound), 2 denied (UnauthorizedAccess), 3 bad request (Argument),
4 internal error, 5 unsupported protocol version (the daemon then closes the connection).

Event kinds: 0 KeyCreated, 1 KeyDeleted, 2 KeyFlagsChanged, 3 ValueSet, 4 ValueDeleted, 5 PermissionsChanged.

After `Subscribe(path)` the client is told about changes at or below `path` ("" for everything), including
changes other processes make to the hive file. It is only told about keys it could look at itself (search
permission on every key down to it; for a deleted key, down to where it was). A client too slow to keep up is
disconnected rather than allowed to stall writers.

## Sockets

| instance | socket | mode | hive | serves |
|---|---|---|---|---|
| system | `/run/fhive/system.sock` | 0666 | `/etc/hive/System.fhive` | everyone (permissions decide) |
| user | `$XDG_RUNTIME_DIR/fhive/user.sock` | 0600 | `~/.config/hive/User.fhive` | that user, and root |

Both run as systemd socket-activated services (`data/systemd/`). A client with no daemon to talk to reads the
hive file directly (file-sharing mode, `docs` in the hive format and `Hive.OpenShared`).
