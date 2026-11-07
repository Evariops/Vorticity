---------------------------- MODULE DatasetLifecycle ----------------------------
(***************************************************************************)
(* The retention window, the vacuum, and the clocks a dataset relies on    *)
(* (docs/design/13-dataset.md, "The latest version in one request" and      *)
(* "Lifecycle"). The code is DatasetCommitter.AttemptAsync, LatestAsync,    *)
(* AfterAsync and NewestFromAsync, VortexDataset.RefreshAsync, and         *)
(* DatasetVacuum.SweepAsync.                                                *)
(*                                                                          *)
(* Time is a count of ticks of the store's clock, which dates every object. *)
(* A writer measures time on its own clock, by the difference of two       *)
(* readings, so a constant offset from the store's cancels out. What does  *)
(* not cancel is a reading that falls behind an earlier one, a clock       *)
(* stepped back, or a pause between a reading and the request it decides:  *)
(* the reading a writer decides by lags the store's time by up to          *)
(* WriterDrift ticks, against an earlier reading taken exact. That is the  *)
(* worst case: a reading ahead only ends the trust, or trips the guard,    *)
(* sooner. The vacuum compares its clock with the store's dates, so its    *)
(* offset counts as it is, up to VacuumSkew either way at each pass. A     *)
(* listing may miss the versions created less than ListingLag ticks ago.   *)
(*                                                                          *)
(* Writers upload an object, then commit it after any delay, and refresh   *)
(* whenever they like. A writer that knew its version less than TrustSpan  *)
(* ago builds on it as it holds it, or asks for the versions after it,     *)
(* where a missing one ends the run; past the trust span, it lists. A      *)
(* commit creates the next version's key if no object holds it, one vacuum *)
(* deleted included, and a writer that loses moves past the key it lost.   *)
(*                                                                          *)
(* Every version names its own commit object and the first one, whose page *)
(* no commit rewrites, so vacuum keeps the first commit and deletes later  *)
(* ones, leaving gaps below the window. Vacuum marks from the latest       *)
(* version it lists and from every version superseded less than the window *)
(* ago, by the date the store gave the next commit, then, in a second      *)
(* step, deletes what nothing marked once the window has passed by the     *)
(* clock it marked with.                                                    *)
(*                                                                          *)
(* Guard is the fix of the commit that names objects put too long ago: a   *)
(* writer refuses to commit objects it put half a window ago or more.      *)
(***************************************************************************)
EXTENDS Integers, FiniteSets, TLC

CONSTANTS
    Writers,        \* each uploads one object, then commits it
    Window,         \* the retention window, in ticks
    TrustSpan,      \* how long after it knew its version a writer asks for the next ones rather than list
    VacuumSkew,     \* the most the vacuum's clock is off the store's, either way, at each pass
    WriterDrift,    \* the most a writer's reading lags the store's time against an earlier one
    ListingLag,     \* a version created less than this many ticks ago may be missing from a listing
    MaxTime,        \* the ticks the model runs for
    MaxVersions,    \* the versions the model creates at most
    Passes,         \* how many times vacuum runs
    Refreshes,      \* switch: writers refresh between their commits, as a handle may
    Guard           \* switch: a writer refuses to commit objects it put half a window ago or more

ASSUME /\ Window \in Nat \ {0} /\ TrustSpan \in Nat /\ VacuumSkew \in Nat /\ WriterDrift \in Nat
       /\ ListingLag \in Nat /\ MaxTime \in Nat /\ MaxVersions \in Nat \ {0} /\ Passes \in Nat
       /\ Refreshes \in BOOLEAN /\ Guard \in BOOLEAN

Versions == 1..MaxVersions
Seed == "seed"
Objects == Writers \cup {Seed}

VARIABLES
    now,        \* the store's clock
    created,    \* [Versions -> time or -1]: when the store created each version's commit object, -1 never
    present,    \* [Versions -> BOOLEAN]: whether the commit object is in the store
    refs,       \* [Versions -> SUBSET Objects]: the objects each version names
    objAt,      \* [Objects -> time or -1]: when the store dated each object, -1 never put
    objHere,    \* [Objects -> BOOLEAN]: whether the object is in the store
    wpc,        \* [Writers -> {"upload", "commit", "done"}]
    known,      \* [Writers -> the version the writer last knew to be the latest]
    knownAt,    \* [Writers -> when it knew it, by its clock]
    held,       \* [Writers -> BOOLEAN]: whether it holds the commit object of that version
    putAt,      \* [Writers -> when it started putting its object, by its clock]
    vpc,        \* the vacuum's step, "mark" or "sweep", and how many passes it made: <<step, passes>>
    vNow,       \* the vacuum's clock when it marked
    vLatest,    \* the version it marked from, the latest its listing showed
    vMarked,    \* the versions and objects it marked
    forked,     \* history: a commit created a key below the latest version, or one vacuum had deleted
    refused,    \* history: the writers the guard turned away
    crossed     \* history: the writers whose object was put and not yet committed when vacuum swept

vars == <<now, created, present, refs, objAt, objHere, wpc, known, knownAt, held, putAt,
          vpc, vNow, vLatest, vMarked, forked, refused, crossed>>

\* Writers are interchangeable: their names, and the objects named after them, can be permuted.
Symmetry == Permutations(Writers)

Here(v) == v \in Versions /\ present[v]
Latest == CHOOSE v \in Versions : present[v] /\ \A u \in Versions : present[u] => u <= v
Pages(v) == {v, 1}

\* The reading a writer decides by: the store's time, or up to WriterDrift ticks behind it.
Readings == {now - o : o \in 0..WriterDrift}

\* What a writer that is done keeps: nothing it acts on again, reset so that its past splits no state.
Finished(w) == /\ wpc' = [wpc EXCEPT ![w] = "done"]
               /\ known' = [known EXCEPT ![w] = 1]
               /\ knownAt' = [knownAt EXCEPT ![w] = 0]
               /\ held' = [held EXCEPT ![w] = TRUE]
               /\ putAt' = [putAt EXCEPT ![w] = 0]

Init == /\ now = 0
        /\ created = [v \in Versions |-> IF v = 1 THEN 0 ELSE -1]
        /\ present = [v \in Versions |-> v = 1]
        /\ refs = [v \in Versions |-> IF v = 1 THEN {Seed} ELSE {}]
        /\ objAt = [o \in Objects |-> IF o = Seed THEN 0 ELSE -1]
        /\ objHere = [o \in Objects |-> o = Seed]
        /\ wpc = [w \in Writers |-> "upload"]
        /\ known = [w \in Writers |-> 1]
        /\ knownAt = [w \in Writers |-> 0]
        /\ held = [w \in Writers |-> TRUE]
        /\ putAt = [w \in Writers |-> 0]
        /\ vpc = <<"mark", 0>>
        /\ vNow = 0
        /\ vLatest = 1
        /\ vMarked = [versions |-> {}, objects |-> {}]
        /\ forked = FALSE
        /\ refused = {}
        /\ crossed = {}

Tick == /\ now < MaxTime
        /\ now' = now + 1
        /\ UNCHANGED <<created, present, refs, objAt, objHere, wpc, known, knownAt, held, putAt,
                       vpc, vNow, vLatest, vMarked, forked, refused, crossed>>

(* ------------------------------ finding the latest ------------------------------ *)

\* What a listing of commit/ may answer for the newest version (NewestVersionAsync,
\* DatasetCommitter.cs:605-611): one no version after which was created ListingLag ticks ago or more.
Listed == {v \in Versions : present[v] /\ \A u \in Versions : (present[u] /\ u > v) => now - created[u] < ListingLag}

RECURSIVE Doubled(_, _)
\* NewestFromAsync's doubling (DatasetCommitter.cs:547-579): heads further and further from a version
\* present, to one absent.
Doubled(p, step) == IF Here(p + step) THEN Doubled(p + step, step * 2) ELSE <<p, p + step>>

RECURSIVE Bisected(_, _)
\* Then a bisection between the two, a version missing taken for the end.
Bisected(lo, hi) == IF hi - lo <= 1 THEN lo
                    ELSE LET mid == lo + ((hi - lo) \div 2) IN
                         IF Here(mid) THEN Bisected(mid, hi) ELSE Bisected(lo, mid)

NewestFrom(p) == LET d == Doubled(p, 1) IN Bisected(d[1], d[2])

\* AfterAsync (DatasetCommitter.cs:506-525): the first version not in hand read while a head asks for
\* the one after it. 0 when it throws, the version it read gone, which sends LatestAsync to the listing.
After(k, h) == LET first == IF h THEN k + 1 ELSE k IN
               IF Here(first + 1) THEN NewestFrom(first + 1)
               ELSE IF Here(first) THEN first
               ELSE IF h THEN k
               ELSE 0

\* LatestAsync (DatasetCommitter.cs:466-489): asked for from the version known while trusted, listed
\* otherwise or when that throws.
Searched(w, t) == LET a == After(known[w], held[w]) IN
                  IF t - knownAt[w] < TrustSpan /\ a # 0 THEN {a} ELSE Listed

(* ------------------------------ the writers ------------------------------ *)

\* The first put of the writer's objects, the clock read before it (VortexDataset.cs:903): dated by the
\* store when it starts, as S3 dates an upload in parts.
Upload(w) ==
    /\ wpc[w] = "upload"
    /\ putAt' = [putAt EXCEPT ![w] = now]
    /\ objAt' = [objAt EXCEPT ![w] = now]
    /\ objHere' = [objHere EXCEPT ![w] = TRUE]
    /\ wpc' = [wpc EXCEPT ![w] = "commit"]
    /\ UNCHANGED <<now, created, present, refs, known, knownAt, held, vpc, vNow, vLatest, vMarked,
                   forked, refused, crossed>>

\* VortexDataset.RefreshAsync (VortexDataset.cs:579-591): the latest found as a commit finds it, taken
\* as known when it was asked.
Refresh(w) ==
    /\ Refreshes
    /\ wpc[w] # "done"
    /\ \E t \in Readings :
          \E f \in Searched(w, t) :
             /\ known' = [known EXCEPT ![w] = f]
             /\ knownAt' = [knownAt EXCEPT ![w] = now]
             /\ held' = [held EXCEPT ![w] = TRUE]
    /\ UNCHANGED <<now, created, present, refs, objAt, objHere, wpc, putAt, vpc, vNow, vLatest, vMarked,
                   forked, refused, crossed>>

\* One attempt of DatasetCommitter.AttemptAsync: the version to build on, held and trusted taken as it
\* is, searched otherwise (DatasetCommitter.cs:212-221); the guard (:408-409, RequireRecentPuts at
\* :437-450); and the creation of the next key if nothing holds it, the search moving past it if
\* another writer took it (:422-423).
Commit(w) ==
    /\ wpc[w] = "commit"
    /\ \E t \in Readings :
          LET outright == t - knownAt[w] < TrustSpan /\ held[w] IN
          IF Guard /\ t - putAt[w] >= Window \div 2
          THEN \* Its object may be gone: the commit refuses, and the writer is done with it.
               /\ Finished(w)
               /\ refused' = refused \cup {w}
               /\ UNCHANGED <<created, present, refs, forked>>
          ELSE \E b \in (IF outright THEN {known[w]} ELSE Searched(w, t)) :
               /\ b + 1 \in Versions
               /\ UNCHANGED refused
               /\ IF ~present[b + 1]
                  THEN \* The key is free: the version is created, on top of the version found.
                       /\ created' = [created EXCEPT ![b + 1] = now]
                       /\ present' = [present EXCEPT ![b + 1] = TRUE]
                       /\ refs' = [refs EXCEPT ![b + 1] = refs[b] \cup {w}]
                       /\ forked' = (forked \/ created[b + 1] # -1 \/ \E u \in Versions : present[u] /\ u > b + 1)
                       /\ Finished(w)
                  ELSE \* Another writer went first: the search starts past it, from the time it asked.
                       /\ known' = [known EXCEPT ![w] = b + 1]
                       /\ knownAt' = [knownAt EXCEPT ![w] = IF outright THEN knownAt[w] ELSE now]
                       /\ held' = [held EXCEPT ![w] = FALSE]
                       /\ UNCHANGED <<created, present, refs, forked, wpc, putAt>>
    /\ UNCHANGED <<now, objAt, objHere, vpc, vNow, vLatest, vMarked, crossed>>

(* ------------------------------ the vacuum ------------------------------ *)

\* The version after v that is still in the store, whose creation superseded v.
Successor(v) == CHOOSE u \in Versions : present[u] /\ u > v /\ \A x \in Versions : (present[x] /\ x > v) => u <= x

\* DatasetVacuum.SweepAsync, its clock read once (DatasetVacuum.cs:140): the latest version its listing
\* shows (:142), and every version below it superseded less than the window ago by the store's date of
\* the next commit (:165-174), marked with the pages and objects they name.
Mark ==
    /\ vpc[1] = "mark" /\ vpc[2] < Passes
    /\ \E s \in -VacuumSkew..VacuumSkew, top \in Listed :
          LET t == now + s
              retained == {top} \cup {v \in Versions : present[v] /\ v < top /\ t - created[Successor(v)] < Window}
          IN /\ vNow' = t
             /\ vLatest' = top
             /\ vMarked' = [versions |-> UNION {Pages(v) : v \in retained}, objects |-> UNION {refs[v] : v \in retained}]
    /\ vpc' = <<"sweep", vpc[2]>>
    /\ UNCHANGED <<now, created, present, refs, objAt, objHere, wpc, known, knownAt, held, putAt, forked,
                   refused, crossed>>

\* Then deletes what nothing marked once the window has passed by the clock it marked with (:371-380),
\* leaving the commits after the one it marked from, which are not its to judge (:158). What it marked is
\* reset after, since nothing reads it until the next pass marks again.
Sweep ==
    /\ vpc[1] = "sweep"
    /\ present' = [v \in Versions |-> present[v] /\ (v > vLatest \/ v \in vMarked.versions \/ vNow - created[v] < Window)]
    /\ objHere' = [o \in Objects |-> objHere[o] /\ (o \in vMarked.objects \/ vNow - objAt[o] < Window)]
    /\ vpc' = <<"mark", vpc[2] + 1>>
    /\ vNow' = 0
    /\ vLatest' = 1
    /\ vMarked' = [versions |-> {}, objects |-> {}]
    /\ crossed' = crossed \cup {w \in Writers : wpc[w] = "commit"}
    /\ UNCHANGED <<now, created, refs, objAt, wpc, known, knownAt, held, putAt, forked, refused>>

Next == \/ Tick \/ Mark \/ Sweep
        \/ \E w \in Writers : Upload(w) \/ Refresh(w) \/ Commit(w)

Spec == Init /\ [][Next]_vars

(* ------------------------------ the properties ------------------------------ *)

\* The latest version names only objects and commit objects the store holds.
LatestReadable == /\ \A o \in refs[Latest] : objHere[o]
                  /\ \A p \in Pages(Latest) : present[p]

\* No commit creates a key below the latest version, or a key vacuum had taken.
NoFork == ~forked

\* A version superseded less than the window ago, less the vacuum's skew, is still whole.
RetainedReadable ==
    \A v \in Versions :
        (present[v] /\ v < Latest /\ now - created[Successor(v)] < Window - VacuumSkew)
            => (\A o \in refs[v] : objHere[o]) /\ (\A p \in Pages(v) : present[p])

\* Witness: vacuum deletes a commit object, leaving a gap below the window.
NoCommitDeleted == \A v \in Versions : created[v] # -1 => present[v]

\* Witness: the guard turns a writer away.
NoRefusal == refused = {}

\* Witness: a writer commits an object it put before a vacuum swept, the guard letting it through.
NoCommitAcrossVacuum == \A w \in crossed : wpc[w] # "done" \/ w \in refused
================================================================================
