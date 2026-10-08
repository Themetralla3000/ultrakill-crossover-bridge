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
