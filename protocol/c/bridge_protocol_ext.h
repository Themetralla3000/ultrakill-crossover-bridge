/*
 * bridge_protocol_ext.h - OPTIONAL extensions to bridge.shm, layered on top of bridge_protocol.h.
 * Hosts that do not know about them (e.g. the Elden Ring DLL) simply never write them; the guest then
 * sees no magic and keeps its default behaviour. Include bridge_protocol.h first.
 */
#pragma once
#include <stdint.h>

/* ErmcHostEvents: run/progress information written by the HOST, read by the guest. */
#define ERMC_OFF_HOST_EVENTS 0x360000u   /* after ERMC_OFF_COLLISION_CONTROL (+48) and well below 8 MiB */
#define ERMC_HOSTEV_MAGIC    0x56454B55u /* bytes "UKEV" */
#define ERMC_HOSTEV_VERSION  1u
#define ERMC_HOSTEV_COUNTERS 16u

/* mode: what loadout the host asks the guest to use. */
#define ERMC_LOADOUT_GUEST        0u  /* host has no opinion: the guest config decides */
#define ERMC_LOADOUT_ALL          1u  /* every weapon available */
#define ERMC_LOADOUT_PROGRESSION  2u  /* start with one weapon, unlock more as counters grow */

/* counters[] indices (monotonic within a run; reset to 0 when runSeed changes) */
#define ERMC_CTR_BOSSES_DEFEATED  0u  /* major/teleporter bosses defeated */
#define ERMC_CTR_STAGES_CLEARED   1u
#define ERMC_CTR_ELITE_KILLS      2u  /* optional */
/* 3..15 reserved (write 0) */

/* ErmcHostEvents.flags (bit set = true). Hosts that predate the bits write 0 = all false. */
#define ERMC_HOSTFLAG_DRAWS_PROMPT  (1u << 0) /* the host shows its own interaction prompt (and key glyph): the guest hides its HUD label */
#define ERMC_HOSTFLAG_NEEDS_INPUT   (1u << 1) /* the host has UI that needs the mouse (picker, shop...): the guest hands input to the host while set, then takes it back */

#define ERMC_HOSTFLAG_STAT_DAMAGE   (1u << 2) /* the host understands STAT damage entries and publishes ErmcHostCombat (below); without it the guest sends the legacy fraction encoding */

#pragma pack(push, 4)
typedef struct ErmcHostEvents {
    uint32_t magic;               /* 0x00 ERMC_HOSTEV_MAGIC; written LAST on first publish */
    uint32_t version;             /* 0x04 ERMC_HOSTEV_VERSION */
    volatile uint32_t seq;        /* 0x08 seqlock (odd = write in progress) */
    uint32_t flags;               /* 0x0C ERMC_HOSTFLAG_* */
    uint32_t loadoutMode;         /* 0x10 ERMC_LOADOUT_* */
    uint32_t reserved0;           /* 0x14 */
    uint64_t runSeed;             /* 0x18 identifies the run; a change means "new run" */
    uint32_t counters[ERMC_HOSTEV_COUNTERS]; /* 0x20 */
} ErmcHostEvents;                 /* 0x60 */
#pragma pack(pop)

#ifdef __cplusplus
static_assert(sizeof(ErmcHostEvents) == 0x60, "host events size");
static_assert(ERMC_OFF_HOST_EVENTS >= ERMC_OFF_COLLISION_CONTROL + 48u, "host events after collision control");
static_assert(ERMC_OFF_HOST_EVENTS + sizeof(ErmcHostEvents) <= ERMC_SHM_SIZE, "host events fit");
#endif

/*
 * ErmcGuestRequests: GUEST -> HOST requests beyond the interact action in the header (mcActionReq). Written by the
 * guest, read by the host; a host that does not know the block ignores it. Counters are monotonic u32s: every
 * increase is one request (compare with `!=` and baseline on the first read, a restarted guest may start lower).
 * Aim for all of them is the guest camera in ErmcControl. Hosts execute them like the player's own input.
 */
#define ERMC_OFF_GUEST_REQUESTS 0x360100u
#define ERMC_GREQ_MAGIC         0x51524B55u /* bytes "UKRQ" */
#define ERMC_GREQ_VERSION       1u

/* ErmcGuestRequests.held: level state of the guest's keys (for hosts that want native hold behaviour) */
#define ERMC_GREQ_HELD_INTERACT   (1u << 0)
#define ERMC_GREQ_HELD_EQUIPMENT  (1u << 1)
#define ERMC_GREQ_HELD_PING       (1u << 2)

#pragma pack(push, 4)
typedef struct ErmcGuestRequests {
    uint32_t magic;               /* 0x00 ERMC_GREQ_MAGIC; written LAST on first publish */
    uint32_t version;             /* 0x04 ERMC_GREQ_VERSION */
    volatile uint32_t seq;        /* 0x08 seqlock (odd = write in progress) */
    uint32_t held;                /* 0x0C ERMC_GREQ_HELD_* */
    uint32_t useEquipment;        /* 0x10 ++ per press of the guest's equipment key */
    uint32_t ping;                /* 0x14 ++ per press of the guest's ping key */
    uint32_t reserved[2];         /* 0x18 write 0 */
    char     interactKey[16];     /* 0x20 UTF-8, NUL-terminated name of the guest's interact key ("V"), for the host's glyph; "" = unknown */
} ErmcGuestRequests;              /* 0x30 */
#pragma pack(pop)

#ifdef __cplusplus
static_assert(sizeof(ErmcGuestRequests) == 0x30, "guest requests size");
static_assert(ERMC_OFF_GUEST_REQUESTS >= ERMC_OFF_HOST_EVENTS + sizeof(ErmcHostEvents), "guest requests after host events");
static_assert(ERMC_OFF_GUEST_REQUESTS + sizeof(ErmcGuestRequests) <= ERMC_SHM_SIZE, "guest requests fit");
#endif

/*
 * Stat damage extension (guest -> host damage ring + host -> guest ErmcHostCombat).
 *
 * The guest sends STAT entries ONLY when the host sets ERMC_HOSTFLAG_STAT_DAMAGE in ErmcHostEvents.flags AND
 * ErmcHostCombat is valid; otherwise it keeps the legacy encoding (amount = fraction of max HP * mc), which is
 * what the Elden Ring host reads. ErmcDamage stays 0x20 bytes; only `flags` bits >= 4 and `reserved` are new.
 *
 *   flags  bit4 STAT      amount = SUM of ULTRAKILL base damage (UK units, before head/crit bonus) of the aggregated hits
 *          bit5 WEAKPOINT at least one aggregated hit hit the head (base damage excludes the head bonus)
 *          bit6 EXPLOSION area hit (hosts add their AOE damage type)
 *          bit7 FRACTION  legacy semantics on a STAT host (parry): amount = fraction of max HP * mc
 *   reserved (u32)
 *          bits  0..5  weapon id (ERMC_WEAPON_*; 0 and 63 = unknown)
 *          bits  6..7  reserved (0)
 *          bits  8..15 hit count 1..255 aggregated in this entry
 *          bits 16..23 shot sequence (wraps): equal for all hits of one trigger pull; hosts key crit rolls on (weapon, seq)
 *          bits 24..27 hit kind: 0 direct, 1 area, 2 damage over time, 3 melee, 4 coin chain
 *          bits 28..31 reserved (0)
 * One entry is written per (target, weapon id, weak point, shot sequence, hit kind) per guest tick.
 * Host damage of an entry: bodyDamage * ErmcHostCombat.damageScale * weapons[id].k * amount (x headshotMultiplier on WEAKPOINT);
 * procCoefficient = min(procCap, weapons[id].proc * hitCount).
 */
#define ERMC_DAMAGE_STAT       (1u << 4)
#define ERMC_DAMAGE_WEAKPOINT  (1u << 5)
#define ERMC_DAMAGE_EXPLOSION  (1u << 6)
#define ERMC_DAMAGE_FRACTION   (1u << 7)

#define ERMC_WEAPON_UNKNOWN         0u
#define ERMC_WEAPON_REV_SHOT        1u  /* revolver primary, all variants (also the beam after a coin) */
#define ERMC_WEAPON_REV_PIERCER     2u  /* charged revolver beam (Piercer / Sharpshooter) */
#define ERMC_WEAPON_REV_MARKSMAN    3u  /* charged revolver beam, Marksman */
#define ERMC_WEAPON_COIN_HIT        4u
#define ERMC_WEAPON_SHO_PELLET      5u
#define ERMC_WEAPON_SHO_ZONE        6u  /* shotgun point blank */
#define ERMC_WEAPON_SHO_OVERCHARGE  7u  /* Pump Charge explosion */
#define ERMC_WEAPON_SHO_GRENADE     8u  /* Core Eject grenade explosion */
#define ERMC_WEAPON_SHO_SAW         9u
#define ERMC_WEAPON_HAMMER          10u
#define ERMC_WEAPON_NAIL            11u
#define ERMC_WEAPON_NAIL_BURST      12u /* reserved, not sent yet */
#define ERMC_WEAPON_SAWBLADE        13u
#define ERMC_WEAPON_ZAPPER          14u
#define ERMC_WEAPON_RAIL_BEAM       15u
#define ERMC_WEAPON_RAIL_MALICIOUS  16u
#define ERMC_WEAPON_RAIL_HARPOON    17u /* harpoon, drill, drill punch */
#define ERMC_WEAPON_ROCKET          18u
#define ERMC_WEAPON_CANNONBALL      19u
#define ERMC_WEAPON_NAPALM          20u
#define ERMC_WEAPON_PUNCH           21u
#define ERMC_WEAPON_KNUCKLE         22u
#define ERMC_WEAPON_WHIP            23u
#define ERMC_WEAPON_SLAM            24u
#define ERMC_WEAPON_PARRY           25u
#define ERMC_WEAPON_EXPLOSION_OTHER 26u
#define ERMC_WEAPON_FIRE_OTHER      27u
#define ERMC_WEAPON_FALLBACK        63u

/* ErmcHostCombat: HOST -> GUEST stats for the stat damage model. Seqlock, magic written last. */
#define ERMC_OFF_HOST_COMBAT   0x361000u
#define ERMC_HOSTCOMBAT_MAGIC  0x42434B55u /* bytes "UKCB" */
#define ERMC_HOSTCOMBAT_VERSION 1u
#define ERMC_COMBAT_STATS_VALID (1u << 0)

#pragma pack(push, 4)
typedef struct ErmcWeaponCoeff {
    float k;                      /* host damage per UK damage point, as a multiple of the damage stat */
    float proc;                   /* procCoefficient per hit */
} ErmcWeaponCoeff;

typedef struct ErmcHostCombat {
    uint32_t magic;               /* 0x00 ERMC_HOSTCOMBAT_MAGIC; written LAST on first publish */
    uint32_t version;             /* 0x04 */
    volatile uint32_t seq;        /* 0x08 seqlock (odd = write in progress) */
    uint32_t flags;               /* 0x0C ERMC_COMBAT_* */
    uint32_t level;               /* 0x10 character level */
    float    damage;              /* 0x14 body damage stat */
    float    attackSpeedRatio;    /* 0x18 reserved for later phases */
    float    critPercent;         /* 0x1C */
    float    critMultiplier;      /* 0x20 */
    float    reserved1[15];       /* 0x24 */
    float    damageScale;         /* 0x60 host balance multiplier */
    float    headshotMultiplier;  /* 0x64 weak point multiplier */
    uint32_t reserved2[6];        /* 0x68 */
    ErmcWeaponCoeff weapons[64];  /* 0x80 indexed by weapon id */
} ErmcHostCombat;                 /* 0x280 */
#pragma pack(pop)

#ifdef __cplusplus
static_assert(sizeof(ErmcWeaponCoeff) == 8, "weapon coeff size");
static_assert(sizeof(ErmcHostCombat) == 0x280, "host combat size");
static_assert(ERMC_OFF_HOST_COMBAT >= ERMC_OFF_GUEST_REQUESTS + sizeof(ErmcGuestRequests), "host combat after guest requests");
static_assert(ERMC_OFF_HOST_COMBAT + sizeof(ErmcHostCombat) <= ERMC_SHM_SIZE, "host combat fits");
#endif
