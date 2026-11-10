# eero cloud API map (from eero Android 26.13.0, decompiled 2026-11-09)

Raw extracts: `endpoints.txt` (404 Retrofit endpoints), `models.txt` (JSON fields), `api_paths.txt`, `path_fragments.txt`.

## Transport
- Base URL: `https://api-user.e2ro.com/` (relative paths like `2.2/account`; resource links like `/2.2/networks/123/devices` resolve against it).
- Headers on every call: `Accept: application/json`, `User-Agent: eero-android/<ver> <system agent>`, `X-Accept-Language: en-US`.
- Auth header: `X-User-Token: <user_token>` (TokenInterceptor; only sent to allowed eero hosts).
- `@Field` methods are form-encoded (`application/x-www-form-urlencoded`); `@Body` methods are JSON (Gson, IDENTITY naming — JSON name = `@SerializedName` or Java field name).
- Response envelope: `{ "meta": { "code": 200, "server_time": "...", "error": "..." }, "data": ... }`.

## Login
| Step | Call | Body |
|---|---|---|
| Start | `POST 2.2/login` | form `login=<email or +phone>` → `data.user_token` |
| Verify | `POST 2.2/login/verify` (with X-User-Token) | form `code=<6-digit code>` → `data` = User |
| Resend code | `POST 2.2/login/resend` | `{}` |
| Refresh session | `POST 2.2/login/refresh` | `{}` |
| Logout | `POST 2.2/logout` | — |
| Account | `GET 2.2/account` | → User (`networks.data[]` = NetworkReference with `url`, `name`) |

## Network (`GET <network.url>` → Network)
Key fields: `name, url, status, health{internet{status,isp_up},eero_network{status}}, wan_ip, gateway_ip, ip_settings{public_ip,double_nat}, connection{mode}, dhcp{mode,custom}, dns{mode,caching,custom{ips},parent}, lease, ipv6_upstream, upnp, band_steering, sqm, wpa3, thread, guest_network{enabled,name,password}, speed{down{value,units},up{...},date}, updates{...}, eeros{data[]}, connected_device_count{on_network}, premium_dns, last_reboot, timezone, resources{...}`.

`resources` links: `devices, eeros, profiles, guestnetwork, password, reservations, forwards, settings, speedtest, reboot, updates, routing, diagnostics, support, thread, transfer, burst_reporters`.

| Feature | Call |
|---|---|
| Rename network | `PUT <network.url>` form `name=` |
| Wi-Fi password | `PUT resources.password` form `password=` |
| Settings (UPnP, band steering, SQM, WPA3, IPv6, DNS, DHCP, DNS caching...) | `PUT resources.settings` JSON = full Network object with the changed field(s) (app mirrors current network) |
| DHCP config | `PUT 2.2/networks/{id}/settings` (saveDhcpConfiguration) |
| Restart whole network | `POST resources.reboot` body `{}` |
| Speed test run / history | `POST resources.speedtest` `{}` / `GET resources.speedtest?limit=&startTime=&endTime=` → SpeedTest[] (`date, down_mbps, up_mbps`) |
| Updates | `GET resources.updates`; `POST resources.updates` `{}` = update now; `POST resources.updates/preferred_update_hour` |
| Guest network | `GET/PUT resources.guestnetwork` form `name=, enabled=`; password `PUT guest.resources.password` form `password=` |
| Reservations | `GET/POST resources.reservations` JSON `{mac, ip, description}`; `PUT/DELETE reservation.url` (`?delete_forwards=`) |
| Port forwards | `GET/POST resources.forwards` JSON `{ip, gateway_port, client_port, protocol(tcp/udp/both), description, enabled}`; `PUT/DELETE forward.url` |
| IPv6 pinholes | `<network.url>/ipv6/pinholes` GET/POST; `<pinhole.url>` PUT/DELETE |
| Routing/firewall rules | `GET <network.url>/routing` → NetRules |
| DNS policies (eero Plus) | `GET/POST <network.url>/dns_policies/network` (form map slug→bool); adblock `POST <network.url>/dns_policies/adblock` JSON `{enable, profiles}`; per-profile `<network.url>/dns_policies/profiles/{id}` |
| Block lists | `GET 2.2/networks/{id}/dns_policies/advanced_content_filter`; `PUT .../dns_policies/network/blocked|allowed` |
| Data usage / insights | `GET 2.2/networks/{id}/data_usage[...]`, `GET 2.2/networks/{id}/insights[...]` |
| Notifications | `GET 2.2/networks/{network_id}/notifications_history` |
| Radio settings, power saving, backup internet, etc. | see endpoints.txt (`2.2/networks/{networkId}/...`) |

## Devices
| Feature | Call |
|---|---|
| List | `GET resources.devices?thread=false&proxied_node=false` → NetworkDevice[] |
| Rename / pause / profile | `PUT /2.3/networks/{id}/devices/{mac}` JSON NetworkDeviceRequest `{nickname, paused, profile:{url}}` |
| Block | `POST <network.url>/blacklist` form `mac=`; unblock `DELETE <network.url>/blacklist/{mac}`; list `GET <network.url>/blacklist` |
NetworkDevice fields: `url, mac, ip, ips, ipv4, nickname, hostname, display_name, manufacturer, model_name, device_type, connected, wireless, connection_type, paused, blacklisted, is_guest, is_private, profile{url,name,paused}, source{location,display_name,url}, connectivity{score_bars, rx_bitrate,...}, interface{frequency, frequency_unit}, ssid, usage{down_mbps,up_mbps}, first_active, last_active`.

## Profiles
`GET/POST resources.profiles` (POST JSON `{name, devices:[{url}]}`); `GET/PUT/DELETE <profile.url>` (PUT JSON `{name?, paused?, devices?}`); schedules `profile.resources.schedules` (ScheduledPause `{name, enabled, start "HH:mm", end, days[]}`).

## eero units (`network.eeros.data[]` or `GET resources.eeros`)
Eero fields: `url, serial, location, model, model_number, mac_address, ip_address, os_version, status, state, gateway, wired, connection_type, mesh_quality_bars, connected_clients_count, connected_wired_clients_count, connected_wireless_clients_count, led_on, led_brightness, update_available, last_reboot, last_heartbeat, nightlight, resources{reboot, led_action, connections}`.
| Feature | Call |
|---|---|
| Restart one eero | `POST eero.resources.reboot` `{}` |
| LED on/off | `PUT eero.resources.led_action` form `led_on=` |
| LED brightness | `PUT eero.resources.led_action` form `led_brightness=` |
| Rename location | `PUT eero.url` form `location=` |
| Firmware history | `GET 2.2/eeros/{id}/firmware_update/history` |
| Nightlight (Beacon) | `GET/PUT <eero.url>/nightlight` |
| Node/port actions | `POST 2.2/eeros/{id}/action`, `POST 2.2/eeros/{id}/ports/{n}/action` |
