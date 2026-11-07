----------------------------- MODULE DatasetCommit -----------------------------
(***************************************************************************)
(* A dataset's commits, and the rebase of each kind of operation onto the  *)
(* version that won (docs/design/13-dataset.md, "The commit protocol"; the  *)
(* code is DatasetCommitter.AttemptAsync and ApplyAsync).                    *)
(*                                                                          *)
(* A version is a tree of objects per level. A writer prepares its         *)
(* operation on the version it read, then commits it: it reads the latest  *)
(* version, re-applies the operation there, and creates the next version's *)
(* key, which only one writer can create (the store contract of the        *)
(* atomic-put model). A writer that loses reads the winner and applies its *)
(* operation again, up to MaxAttempts times. A batch in which nothing      *)
(* applied publishes nothing. A put may land and its writer die before it  *)
(* learns it, which leaves the version and no answer.                      *)
(*                                                                          *)
(* Appenders add one row each, in an object of its own in level 0.         *)
(* Deleters mark the rows of one key in every object that holds one.       *)
(* Compactors merge level 0 into a level above, or push an object of a     *)
(* level up into the next, moving it whole when nothing there overlaps it. *)
(* Above level 0, a level's objects must not share a key.                  *)
(*                                                                          *)
(* Three switches put back defects the code once had or could have:        *)
(* UidInKey, the uid at the end of an object's tree key; RewalkDestination,*)
(* the look at the destination's range at commit; CheckExpected, the check *)
(* that an input's entry is the one the job read, marks included. A fourth,*)
(* PhantomExists, breaks the store contract instead: a store that answers  *)
(* Exists for a creation its own put made, which PutOutcome.Exists forbids *)
(* ("Nothing was written"), sends the writer to rebase onto its own commit.*)
(***************************************************************************)
EXTENDS Naturals, FiniteSets, Sequences

CONSTANTS
    Appenders,          \* each appends one row, under a key it picks
    Deleters,           \* each deletes the rows of a key it picks
    Compactors,         \* each runs up to Jobs jobs
    Keys,               \* the sort key's values, a few integers
    MaxLevel,           \* the levels are 0..MaxLevel
    Jobs,               \* the jobs each compactor runs
    MaxAttempts,        \* DatasetOptions.MaxAttempts
    Crashes,            \* whether a writer may die once its put has landed, before it learns it
    PhantomExists,      \* switch: the store may answer Exists for a creation its own put made
    Seed,               \* whether the first version holds an object in level 1
    UidInKey,           \* switch: the tree key ends with the object's uid
    RewalkDestination,  \* switch: a leveled replacement looks at its destination's range again at commit
    CheckExpected       \* switch: a replacement checks that each input's entry is the one it read

ASSUME MaxLevel \in Nat \ {0} /\ Jobs \in Nat /\ MaxAttempts \in Nat \ {0}
ASSUME Keys \subseteq Nat /\ Keys # {}

Levels == 0..MaxLevel
Actors == Appenders \cup Deleters \cup Compactors
SeedRow == "seed"
Rows == Appenders \cup (IF Seed THEN {SeedRow} ELSE {})
Max(S) == CHOOSE m \in S : \A n \in S : n <= m
Min(S) == CHOOSE m \in S : \A n \in S : m <= n
NoRange == [level |-> 0, lo |-> 0, hi |-> 0]

VARIABLES
    history,    \* the versions created, each [Levels -> SUBSET entry]; version v is history[v]
    rowKey,     \* [Rows -> Keys \cup {0}]: the key of each row, 0 before it is appended
    pc,         \* [Actors -> {"plan", "read", "put", "done"}]
    op,         \* [Actors -> the operation prepared, or "none"]
    base,       \* [Actors -> the version an attempt read]
    attempts,   \* [Actors -> the attempts lost so far]
    jobs,       \* [Compactors -> the jobs started]
    outcome,    \* [Actors -> what the last attempt decided]
    acked,      \* the rows whose append its writer learned had landed
    erased      \* the rows a version marked deleted

vars == <<history, rowKey, pc, op, base, attempts, jobs, outcome, acked, erased>>

Live(e) == e.rows \ e.marked
Overlap(e, lo, hi) == ~(e.hi < lo \/ hi < e.lo)
Latest == history[Len(history)]
Entries(t) == UNION {{<<l, e>> : e \in t[l]} : l \in Levels}

SeedEntry == [uid |-> <<"seed">>, rows |-> {SeedRow}, marked |-> {}, lo |-> Max(Keys), hi |-> Max(Keys)]
FirstTree == [l \in Levels |-> IF Seed /\ l = 1 THEN {SeedEntry} ELSE {}]

Init == /\ history = <<FirstTree>>
        /\ rowKey = [r \in Rows |-> IF r = SeedRow THEN Max(Keys) ELSE 0]
        /\ pc = [x \in Actors |-> "plan"]
        /\ op = [x \in Actors |-> "none"]
        /\ base = [x \in Actors |-> 0]
        /\ attempts = [x \in Actors |-> 0]
        /\ jobs = [c \in Compactors |-> 0]
        /\ outcome = [x \in Actors |-> "none"]
        /\ acked = IF Seed THEN {SeedRow} ELSE {}
        /\ erased = {}

(* ------------------------------ the rebase ------------------------------ *)

\* Two entries share a tree key: with the uid at its end, only the same object does (DatasetOperation.AddObject).
Collides(y, e) == IF UidInKey THEN y.uid = e.uid ELSE y.lo = e.lo

\* An operation re-applied to a version's tree: the tree it leaves, and what the operation decided
\* (ApplyAsync, DatasetCommitter.cs:928-1125).
Apply(t, o) ==
    IF o.kind = "add"
    THEN LET e == o.entry IN
         IF \E y \in t[0] : y.uid = e.uid
         THEN <<t, "AlreadyThere">>
         ELSE <<[t EXCEPT ![0] = {y \in @ : ~Collides(y, e)} \cup {e}], "Applied">>
    ELSE LET taken(l) == {i[2].uid : i \in {j \in o.inputs : j[1] = l}}
             present(i) == \E y \in t[i[1]] : y.uid = i[2].uid /\ (~CheckExpected \/ y = i[2])
             foreign == RewalkDestination /\ o.range # NoRange
                        /\ \E y \in t[o.range.level] :
                             y.uid \notin taken(o.range.level) /\ Overlap(y, o.range.lo, o.range.hi)
         IN IF (\E i \in o.inputs : ~present(i)) \/ foreign
            THEN <<t, "Abandoned">>
            ELSE <<[l \in Levels |-> {y \in t[l] : y.uid \notin taken(l)}
                                     \cup {k[2] : k \in {m \in o.outputs : m[1] = l}}], "Applied">>

(* ------------------------------ the plans ------------------------------ *)

\* The one object a merge of these inputs writes into level d, from their live rows; none when no row is left.
Merged(c, d, inputs) ==
    LET live == UNION {Live(i[2]) : i \in inputs} IN
    IF live = {} THEN {}
    ELSE {<<d, [uid |-> <<"job", c, jobs[c] + 1>>, rows |-> live, marked |-> {},
                lo |-> Min({rowKey[r] : r \in live}), hi |-> Max({rowKey[r] : r \in live})]>>}

\* Level 0 emptied into level d, with the objects of d it overlaps (DatasetCompactor, level 0's job).
EmptyLevelZero(c, t, d) ==
    LET sources == {<<0, e>> : e \in t[0]}
        lo == Min({e.lo : e \in t[0]})
        hi == Max({e.hi : e \in t[0]})
        meets == {<<d, y>> : y \in {z \in t[d] : Overlap(z, lo, hi)}}
    IN [kind |-> "replace", inputs |-> sources \cup meets, outputs |-> Merged(c, d, sources \cup meets),
        range |-> [level |-> d, lo |-> lo, hi |-> hi], src |-> 0, erases |-> {}]

\* An object of level l pushed into l + 1: moved whole when nothing there overlaps it and it holds no
\* marks, merged with what it overlaps otherwise (CompactionPlan, a trivial move).
PushUp(c, t, l, x) ==
    LET meets == {<<l + 1, y>> : y \in {z \in t[l + 1] : Overlap(z, x.lo, x.hi)}}
        inputs == {<<l, x>>} \cup meets
    IN [kind |-> "replace", inputs |-> inputs,
        outputs |-> IF meets = {} /\ x.marked = {} THEN {<<l + 1, x>>} ELSE Merged(c, l + 1, inputs),
        range |-> [level |-> l + 1, lo |-> x.lo, hi |-> x.hi], src |-> l, erases |-> {}]

Plans(c, t) ==
    (IF t[0] = {} THEN {} ELSE {EmptyLevelZero(c, t, d) : d \in 1..MaxLevel})
    \cup UNION {{PushUp(c, t, l, x) : x \in t[l]} : l \in 1..(MaxLevel - 1)}

\* A delete of the rows of key k: each object that holds a live one, its entry re-marked, or taken out
\* whole when the delete takes every row it still holds (DatasetRowChanges.cs:161-165 and :305-308, the
\* case its bounds prove included, Covers at :102-106). A delete that rewrites an object
\* whose marks would pass its share is left out: the tests that hold the code to this model mark always.
Marks(e, k) == {r \in Live(e) : rowKey[r] = k}

Deletion(t, k) ==
    LET targets == {i \in Entries(t) : Marks(i[2], k) # {}}
        whole(i) == Marks(i[2], k) = Live(i[2])
    IN [kind |-> "replace", inputs |-> targets,
        outputs |-> {<<i[1], [i[2] EXCEPT !.marked = @ \cup Marks(i[2], k)]>> : i \in {j \in targets : ~whole(j)}},
        range |-> NoRange, src |-> MaxLevel + 1, erases |-> UNION {Marks(i[2], k) : i \in targets}]

(* ------------------------------ the actors ------------------------------ *)

Keep == <<base, attempts, outcome, acked, erased>>

PlanAppend(a) ==
    /\ a \in Appenders /\ pc[a] = "plan"
    /\ \E k \in Keys :
         /\ rowKey' = [rowKey EXCEPT ![a] = k]
         /\ op' = [op EXCEPT ![a] = [kind |-> "add", erases |-> {},
                     entry |-> [uid |-> <<"append", a>>, rows |-> {a}, marked |-> {}, lo |-> k, hi |-> k]]]
    /\ pc' = [pc EXCEPT ![a] = "read"]
    /\ UNCHANGED <<history, jobs, Keep>>

PlanDelete(d) ==
    /\ d \in Deleters /\ pc[d] = "plan"
    /\ \E k \in Keys :
         LET o == Deletion(Latest, k) IN
         IF o.inputs = {}
         THEN pc' = [pc EXCEPT ![d] = "done"] /\ UNCHANGED op
         ELSE op' = [op EXCEPT ![d] = o] /\ pc' = [pc EXCEPT ![d] = "read"]
    /\ UNCHANGED <<history, rowKey, jobs, Keep>>

PlanCompaction(c) ==
    /\ c \in Compactors /\ pc[c] = "plan"
    /\ IF jobs[c] < Jobs /\ Plans(c, Latest) # {}
       THEN /\ \E o \in Plans(c, Latest) : op' = [op EXCEPT ![c] = o]
            /\ jobs' = [jobs EXCEPT ![c] = @ + 1]
            /\ pc' = [pc EXCEPT ![c] = "read"]
       ELSE /\ pc' = [pc EXCEPT ![c] = "done"]
            /\ UNCHANGED <<op, jobs>>
    /\ UNCHANGED <<history, rowKey, Keep>>

\* LatestAsync: the version the attempt builds on (DatasetCommitter.cs:207-237).
Read(x) ==
    /\ pc[x] = "read"
    /\ base' = [base EXCEPT ![x] = Len(history)]
    /\ pc' = [pc EXCEPT ![x] = "put"]
    /\ UNCHANGED <<history, rowKey, op, attempts, jobs, outcome, acked, erased>>

\* What an actor does once its operation is decided for good.
Settle(x, o) ==
    /\ outcome' = [outcome EXCEPT ![x] = o]
    /\ acked' = IF x \in Appenders /\ o \in {"Applied", "AlreadyThere"} THEN acked \cup {x} ELSE acked
    /\ pc' = [pc EXCEPT ![x] = IF (x \in Compactors) \/ (x \in Deleters /\ o = "Abandoned" /\ attempts[x] + 1 < MaxAttempts)
                                THEN "plan" ELSE "done"]
    /\ attempts' = [attempts EXCEPT ![x] = IF x \in Compactors THEN 0
                                           ELSE IF x \in Deleters /\ o = "Abandoned" THEN @ + 1 ELSE @]

\* Lost to another writer, or not told: the same operation again on the version after, while attempts
\* remain. A compactor out of attempts gives the job up and plans the next one.
Again(x) ==
    /\ IF attempts[x] + 1 < MaxAttempts
       THEN /\ attempts' = [attempts EXCEPT ![x] = @ + 1]
            /\ pc' = [pc EXCEPT ![x] = "read"]
       ELSE /\ attempts' = [attempts EXCEPT ![x] = IF x \in Compactors THEN 0 ELSE @ + 1]
            /\ pc' = [pc EXCEPT ![x] = IF x \in Compactors THEN "plan" ELSE "done"]
    /\ outcome' = [outcome EXCEPT ![x] = "Lost"]
    /\ UNCHANGED acked

\* The operation re-applied on the version read, then the conditional creation of the next one
\* (DatasetCommitter.cs:279-423).
Put(x) ==
    /\ pc[x] = "put"
    /\ LET r == Apply(history[base[x]], op[x]) IN
       IF r[2] # "Applied"
       THEN \* Nothing applied: the batch publishes nothing and names the version it read.
            /\ Settle(x, r[2])
            /\ UNCHANGED <<history, erased>>
       ELSE IF Len(history) = base[x]
            THEN /\ history' = Append(history, r[1])
                 /\ erased' = erased \cup op[x].erases
                 /\ \/ Settle(x, "Applied")
                    \/ /\ Crashes
                       /\ pc' = [pc EXCEPT ![x] = "done"]
                       /\ outcome' = [outcome EXCEPT ![x] = "Unknown"]
                       /\ UNCHANGED <<attempts, acked>>
                    \/ PhantomExists /\ Again(x)
            ELSE /\ Again(x)
                 /\ UNCHANGED <<history, erased>>
    /\ UNCHANGED <<rowKey, op, base, jobs>>

Next == \E x \in Actors :
            \/ PlanAppend(x) \/ PlanDelete(x) \/ PlanCompaction(x)
            \/ Read(x) \/ Put(x)

Spec == Init /\ [][Next]_vars

(* ------------------------------ the properties ------------------------------ *)

Copies(r, t) == Cardinality({i \in Entries(t) : r \in Live(i[2])})

\* No row is ever read twice, in any version.
NoDuplicate == \A r \in Rows : Copies(r, Latest) <= 1

\* A row whose append its writer was told landed, and that no version deleted, is in the latest version.
NoLostRow == \A r \in acked \ erased : Copies(r, Latest) = 1

\* A row a version deleted comes back in no later version.
NoResurrection == \A r \in erased : Copies(r, Latest) = 0

\* Above level 0, no two objects of a level share a key.
KeyDisjoint == \A l \in 1..MaxLevel : \A e1, e2 \in Latest[l] : e1 # e2 => (e1.hi < e2.lo \/ e2.hi < e1.lo)

\* Witness: two compactions from two levels into one level are in flight together.
NoCrossLevelRace ==
    ~\E c1, c2 \in Compactors :
        /\ c1 # c2
        /\ pc[c1] \in {"read", "put"} /\ pc[c2] \in {"read", "put"}
        /\ op[c1].range.level = op[c2].range.level /\ op[c1].src # op[c2].src

\* Witness: an operation is abandoned on the version it lands on.
NoAbandon == \A x \in Actors : outcome[x] # "Abandoned"

\* Witness: a delete marks rows in an object a compaction read before them.
NoMarkUnderJob ==
    ~\E d \in Deleters, c \in Compactors :
        /\ pc[c] \in {"read", "put"} /\ outcome[d] = "Applied"
        /\ \E i \in op[c].inputs : \E y \in Latest[i[1]] : y.uid = i[2].uid /\ y # i[2]
================================================================================
