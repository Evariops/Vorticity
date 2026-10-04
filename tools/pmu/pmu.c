// Per-thread hardware counters on Apple Silicon, through the private kperf and kperfdata frameworks
// (the approach of ibireme's kpc demo). Configuring the counters takes root: run the measuring
// process under sudo. Built by tools/pmu/build.sh into tools/pmu/out/libpmu.dylib, for the
// benchmark tool only.
#include <dlfcn.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

typedef struct kpep_db kpep_db;
typedef struct kpep_config kpep_config;
typedef struct kpep_event kpep_event;
typedef uint64_t kpc_config_t;

#define KPC_CLASS_FIXED_MASK (1u << 0)
#define KPC_CLASS_CONFIGURABLE_MASK (1u << 1)
#define KPC_MAX_COUNTERS 32

static int (*kpc_force_all_ctrs_set)(int);
static int (*kpc_set_config)(uint32_t, kpc_config_t *);
static int (*kpc_set_counting)(uint32_t);
static int (*kpc_set_thread_counting)(uint32_t);
static int (*kpc_get_thread_counters)(uint32_t, uint32_t, uint64_t *);
static int (*kpep_db_create)(const char *, kpep_db **);
static int (*kpep_config_create)(kpep_db *, kpep_config **);
static int (*kpep_config_force_counters)(kpep_config *);
static int (*kpep_db_event)(kpep_db *, const char *, kpep_event **);
static int (*kpep_config_add_event)(kpep_config *, kpep_event **, uint32_t, uint32_t *);
static int (*kpep_config_kpc_classes)(kpep_config *, uint32_t *);
static int (*kpep_config_kpc_count)(kpep_config *, size_t *);
static int (*kpep_config_kpc_map)(kpep_config *, size_t *, size_t);
static int (*kpep_config_kpc)(kpep_config *, kpc_config_t *, size_t);

static size_t counter_map[KPC_MAX_COUNTERS];
static int event_count;

#define LOAD(lib, name) do { *(void **)&name = dlsym(lib, #name); if (!name) { fprintf(stderr, "pmu: no %s\n", #name); return -1; } } while (0)

// Configures the named events (kpep names, e.g. INST_ALL); returns 0, or a negative step that failed.
int pmu_init(const char **names, int count)
{
    void *kperf = dlopen("/System/Library/PrivateFrameworks/kperf.framework/kperf", RTLD_LAZY);
    void *kperfdata = dlopen("/System/Library/PrivateFrameworks/kperfdata.framework/kperfdata", RTLD_LAZY);
    if (!kperf || !kperfdata) { fprintf(stderr, "pmu: cannot load the frameworks\n"); return -1; }
    LOAD(kperf, kpc_force_all_ctrs_set); LOAD(kperf, kpc_set_config); LOAD(kperf, kpc_set_counting);
    LOAD(kperf, kpc_set_thread_counting); LOAD(kperf, kpc_get_thread_counters);
    LOAD(kperfdata, kpep_db_create); LOAD(kperfdata, kpep_config_create); LOAD(kperfdata, kpep_config_force_counters);
    LOAD(kperfdata, kpep_db_event); LOAD(kperfdata, kpep_config_add_event); LOAD(kperfdata, kpep_config_kpc_classes);
    LOAD(kperfdata, kpep_config_kpc_count); LOAD(kperfdata, kpep_config_kpc_map); LOAD(kperfdata, kpep_config_kpc);

    kpep_db *db; kpep_config *cfg;
    if (kpep_db_create(NULL, &db)) { fprintf(stderr, "pmu: no event database for this CPU\n"); return -2; }
    if (kpep_config_create(db, &cfg)) return -3;
    if (kpep_config_force_counters(cfg)) return -4;
    for (int i = 0; i < count; i++) {
        kpep_event *ev = NULL;
        if (kpep_db_event(db, names[i], &ev) || !ev) { fprintf(stderr, "pmu: unknown event %s\n", names[i]); return -5; }
        uint32_t err = 0;
        if (kpep_config_add_event(cfg, &ev, 0, &err)) { fprintf(stderr, "pmu: cannot count %s (%u)\n", names[i], err); return -6; }
    }

    uint32_t classes = 0; size_t regs = 0;
    kpc_config_t config[KPC_MAX_COUNTERS] = {0};
    if (kpep_config_kpc_classes(cfg, &classes) || kpep_config_kpc_count(cfg, &regs)) return -7;
    if (kpep_config_kpc_map(cfg, counter_map, sizeof(counter_map))) return -8;
    if (kpep_config_kpc(cfg, config, sizeof(config))) return -9;
    if (kpc_force_all_ctrs_set(1)) { fprintf(stderr, "pmu: the counters need root (run under sudo)\n"); return -10; }
    if ((classes & KPC_CLASS_CONFIGURABLE_MASK) && regs && kpc_set_config(classes, config)) return -11;
    if (kpc_set_counting(classes) || kpc_set_thread_counting(classes)) return -12;
    event_count = count;
    return 0;
}

// The current thread's counts of the configured events, in their order.
int pmu_read(uint64_t *values)
{
    uint64_t counters[KPC_MAX_COUNTERS] = {0};
    if (kpc_get_thread_counters(0, KPC_MAX_COUNTERS, counters)) return -1;
    for (int i = 0; i < event_count; i++) values[i] = counters[counter_map[i]];
    return 0;
}
