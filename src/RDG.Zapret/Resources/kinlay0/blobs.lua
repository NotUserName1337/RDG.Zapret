local log = DLOG or print

-- RDG.Zapret: blob loader for the community kinlay0 strategies.
-- Files live in bin/fake; the loader also checks legacy locations.
local blobs = {
    active_discord  = "ACTIVE_DISCORD_UDP.bin",
    active_game     = "ACTIVE_GAME_UDP.bin",
    ch_clone        = "tls_clienthello_www_google_com.bin",
    quic_4pda       = "quic_initial_4pda_to.bin",
    quic_5ka        = "quic_initial_5ka_ru.bin",
    quic_google     = "quic_initial_www_google_com.bin",
    quic_lemana     = "quic_initial_lemana_pro.bin",
    quic_rutube     = "quic_initial_rutube_ru.bin",
    quic_steam      = "quic_initial_steamcommunity_com.bin",
    quic_steamchat  = "quic_initial_steam_chat_com.bin",
    quic_tencent    = "quic_initial_tencent_com.bin",
    stun            = "stun.bin",
    stun2           = "stun2.bin",
    tls_4pda        = "tls_clienthello_4pda_to.bin",
    tls_5ka         = "tls_clienthello_5ka_ru.bin",
    tls_awswaf      = "tls_clienthello_awswaf_com.bin",
    tls_google      = "tls_clienthello_www_google_com.bin",
    tls_mail        = "tls_clienthello_sochi_park.bin",
    tls_onetrust    = "tls_clienthello_max_ru.bin",
    tls_retext      = "tls_clienthello_steam_chat_com.bin",
    tls_sferum      = "tls_clienthello_www_sferum_ru.bin",
    tls_steamchat   = "tls_clienthello_steam_chat_com.bin",
    tls_teamspeak   = "tls_clienthello_teamspeak_com.bin",
    tls_firpo       = "tls_clienthello_www_google_com.bin",
}

local folders = { "fake/", "", "bin/fake/", "bin/" }
local exedir = os.getenv("EXEDIR")
if exedir and exedir ~= "" then
    table.insert(folders, exedir .. "/fake/")
    table.insert(folders, exedir .. "/")
end

local function load(file)
    for _, folder in ipairs(folders) do
        local path = folder .. file
        local f = io.open(path, "rb")
        if f then
            local data = f:read("*a")
            f:close()
            if data and data ~= "" then
                return data
            end
        end
    end
    return nil
end

local count = 0
local problems = {}
for name, file in pairs(blobs) do
    local data = load(file)
    if data then
        _G[name] = data
        count = count + 1
    else
        table.insert(problems, name .. " : " .. file)
    end
end

if #problems > 0 then
    log("blobs.lua: missing blobs: " .. table.concat(problems, ", "))
end
log("blobs.lua: " .. count .. " blobs loaded")
