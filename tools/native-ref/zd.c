// The native timing harness. It builds the reference frame - 65,536 f64 of a random walk rounded to
// the hundredth, compressed at level 3 by the first library - and times ZSTD_decompressDCtx in every
// library that follows, alternating them on each repetition so that machine noise lands on all of
// them alike. Each library's output is checked against the original values before anything is timed.
//
//   zd [-o <dir>] <compressing-lib> <timed-lib>...
//
// -o writes the frame and the raw values to <dir>/reference.zst and <dir>/reference.bin, so that the
// .NET side measures exactly the same bytes (the walk depends on the C library's rand()).
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <dlfcn.h>
#include <math.h>
#include <mach/mach_time.h>

typedef size_t (*compress_t)(void*, size_t, const void*, size_t, int);
typedef void* (*createD_t)(void);
typedef size_t (*dec_t)(void*, void*, size_t, const void*, size_t);
typedef unsigned (*iserr_t)(size_t);

static double ns(uint64_t t) {
  static mach_timebase_info_data_t tb;
  if (!tb.denom) mach_timebase_info(&tb);
  return (double)t * tb.numer / tb.denom;
}

static int cmp(const void* a, const void* b) {
  double x = *(const double*)a, y = *(const double*)b;
  return x < y ? -1 : x > y;
}

static void write_file(const char* dir, const char* name, const void* data, size_t size) {
  char path[4096];
  snprintf(path, sizeof path, "%s/%s", dir, name);
  FILE* f = fopen(path, "wb");
  if (!f || fwrite(data, 1, size, f) != size) { perror(path); exit(1); }
  fclose(f);
}

int main(int argc, char** argv) {
  const char* outDir = NULL;
  int arg = 1;
  if (argc > 2 && strcmp(argv[1], "-o") == 0) { outDir = argv[2]; arg = 3; }
  if (argc - arg < 2) {
    fprintf(stderr, "usage: zd [-o <dir>] <compressing-lib> <timed-lib>...\n");
    return 2;
  }

  size_t n = 65536;
  double* values = malloc(n * 8);
  srand(42);
  double x = 1000.0;
  for (size_t i = 0; i < n; i++) { x += (rand() % 2001 - 1000) / 100.0; values[i] = round(x * 100) / 100; }

  void* ref = dlopen(argv[arg], RTLD_NOW | RTLD_LOCAL);
  if (!ref) { printf("dlopen %s: %s\n", argv[arg], dlerror()); return 1; }
  compress_t comp = (compress_t)dlsym(ref, "ZSTD_compress");
  size_t cap = n * 8 + 4096;
  char* c = malloc(cap);
  size_t clen = comp(c, cap, values, n * 8, 3);
  printf("reference frame: %zu -> %zu bytes\n", n * 8, clen);
  if (outDir) {
    write_file(outDir, "reference.zst", c, clen);
    write_file(outDir, "reference.bin", values, n * 8);
  }

  char* out = malloc(n * 8);
  int libs = argc - arg - 1;
  if (libs > 16) libs = 16;
  void* d[16];
  dec_t f[16];
  for (int l = 0; l < libs; l++) {
    void* h = dlopen(argv[arg + 1 + l], RTLD_NOW | RTLD_LOCAL);
    if (!h) { printf("dlopen %s: %s\n", argv[arg + 1 + l], dlerror()); return 1; }
    d[l] = ((createD_t)dlsym(h, "ZSTD_createDCtx"))();
    f[l] = (dec_t)dlsym(h, "ZSTD_decompressDCtx");
    iserr_t isErr = (iserr_t)dlsym(h, "ZSTD_isError");
    memset(out, 0, n * 8);
    size_t r = f[l](d[l], out, n * 8, c, clen);
    if ((isErr && isErr(r)) || r != n * 8 || memcmp(out, values, n * 8) != 0) {
      printf("%s: wrong output\n", argv[arg + 1 + l]);
      return 1;
    }
  }

#define R 300
  static double t[16][R];
  for (int r = 0; r < R; r++)
    for (int l = 0; l < libs; l++) {
      uint64_t a = mach_absolute_time();
      f[l](d[l], out, n * 8, c, clen);
      t[l][r] = ns(mach_absolute_time() - a);
    }
  printf("%-44s %9s %9s %9s %9s\n", "library", "min", "p25", "median", "p75");
  for (int l = 0; l < libs; l++) {
    qsort(t[l], R, sizeof(double), cmp);
    printf("%-44s %7.1f us %7.1f us %7.1f us %7.1f us\n", argv[arg + 1 + l],
           t[l][0] / 1000, t[l][R / 4] / 1000, t[l][R / 2] / 1000, t[l][3 * R / 4] / 1000);
  }
  return 0;
}
