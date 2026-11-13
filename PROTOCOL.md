# BedReach protocol (v1)

Implemented in `core/Server.cs` + `core/Crypto.cs` and
`android/app/src/main/java/com/bedreach/app/net/{Protocol,Crypto}.kt`. Change both together.

## Transport

- **Discovery**: the phone sends the UDP datagram `BEDREACH_DISCOVER_V1` to broadcast port **47801**.
  The PC replies (unicast) with `{"app":"bedreach","v":1,"name":"…","id":"<serverId>","port":47800}`.
- **Control**: TCP port **47800**. Frames are a 4-byte big-endian length followed by the payload (max 64 KiB).
  Frames are UTF-8 JSON until authentication finishes, then encrypted (see below).

Notation: `||` concatenation, `H` = SHA-256, `HMAC` = HMAC-SHA256, `HKDF` = HKDF-SHA256 with 32-byte output.
Binary values are base64 in JSON. Public keys are X.509 SubjectPublicKeyInfo for P-256.

## Pairing (numeric comparison with commitment)

```
phone → PC  {"t":"pair_hello","device":name,"commit":H(phonePub||phoneNonce)}
PC → phone  {"t":"pair_server","pub":pcPub,"nonce":pcNonce,"name":…,"id":serverId}
phone → PC  {"t":"pair_reveal","pub":phonePub,"nonce":phoneNonce}
```

The PC checks the commitment. Then both sides compute:

```
shared     = ECDH(P-256)   (raw x-coordinate)
transcript = phonePub || phoneNonce || pcPub || pcNonce
secret     = HKDF(shared, salt = H(transcript), info = "bedreach-pair-v1")
code       = uint32be(H("bedreach-code-v1" || transcript || shared)[0..4]) mod 1_000_000
```

Both screens show `code`, and the user approves on the PC:

```
PC → phone  {"t":"pair_ok","deviceId":id,"mac":HMAC(secret,"paired"||deviceId)}   or {"t":"pair_denied"}
```

Because the phone commits before seeing the PC's key, an active attacker has to pick its keys without
knowing the final code, which gives it a 1-in-a-million chance per attempt (and the user sees every attempt).

## Session

```
phone → PC  {"t":"auth","deviceId":id,"nonce":nP}
PC → phone  {"t":"challenge","nonce":nS}                       (or {"t":"auth_fail"})
phone → PC  {"t":"auth_proof","mac":HMAC(secret,"client"||nP||nS)}
PC → phone  {"t":"auth_ok","mac":HMAC(secret,"server"||nP||nS)}   (or {"t":"auth_fail"})
key = HKDF(secret, salt = nP||nS, info = "bedreach-session-v1")
```

After that, every frame is `counter(8 BE) || AES-256-GCM(ciphertext||tag16)` with
nonce = `dir(1) || 000000 || counter(8 BE)`, where `dir` is 0 for phone→PC and 1 for PC→phone.
Counters start at 1 and must strictly increase.

## Messages (encrypted)

PC → phone: `{"t":"info","name":…,"services":[{"id","name","color","app"}]}` (sent first; `app` is true when the PC will open an installed app rather than the website), `{"t":"pong"}`.

Phone → PC:

| `t` | fields | notes |
| --- | --- | --- |
| `ping` | | every 5 s. The PC drops a session after 30 s without traffic |
| `key` | `k` | whitelisted names: `space enter esc backspace tab left up right down pageup pagedown home end f m k j l c s f11 f5 playpause next prev stop volup voldown mute alttab alt_f4 browser_back shift_n win_d` |
| `text` | `s` | at most 500 characters, typed as Unicode |
| `move` | `dx`,`dy` | relative mouse movement, each clamped to ±500 |
| `click` | `b` | `left`, `right` or `middle` |
| `scroll` | `d` | wheel delta, 120 = one notch |
| `launch` | `id` | must match a service in the PC's config |
| `power` | `a` | `sleep`, `lock`, `shutdown` (60 s) or `cancel` |

Unknown messages are ignored.
