---------------------------- MODULE DatasetSources ----------------------------
(***************************************************************************)
(* The sources and offsets that count each entry of a source once, however *)
(* many writers take it (docs/design/13-dataset.md, "Sources and offsets";*)
(* the code is DatasetCommitter.AttemptAsync and SourceTable).             *)
(*                                                                          *)
(* A version records, for the one source here, the offset up to which its  *)
(* entries are counted. A writer takes a range (from, to] of entries, from *)
(* the offset of the version it holds, and commits their rows declaring    *)
(* the range. Every attempt judges the range against the version it lands *)
(* on: it applies when the offset is its start, is counted already when the*)
(* offset is past its end, and conflicts otherwise. A verdict other than   *)
(* applying stands only on a version the store said was the latest: one   *)
(* the writer held may have been passed, and the attempt asks for the      *)
(* latest and judges again. A writer whose put lands and who never learns  *)
(* it takes its range again, as a writer started again would, in a new     *)
(* commit of new rows.                                                     *)
(*                                                                          *)
(* Two switches put back what the rules guard against: JudgeEachAttempt    *)
(* judges the range on the version each attempt lands on rather than once  *)
(* on the version the writer prepared on, and AskBeforeVerdict asks for the*)
(* latest version before a verdict other than applying stands.             *)
(***************************************************************************)
EXTENDS Naturals, FiniteSets, Sequences

CONSTANTS
    Writers,            \* the writers that take entries of the source
    Entries,            \* how many entries the source has, 1..Entries
    MaxAttempts,        \* DatasetOptions.MaxAttempts
    Crashes,            \* whether a put may land and its writer take the range again without knowing
    JudgeEachAttempt,   \* switch: the range is judged on the version each attempt lands on
    AskBeforeVerdict    \* switch: a verdict other than applying stands only on the latest version

ASSUME Entries \in Nat \ {0} /\ MaxAttempts \in Nat \ {0}

VARIABLES
    history,    \* the versions, each [offset |-> n, rows |-> set of <<commit, entry>>]
    pc,         \* [Writers -> {"take", "attempt", "put", "done"}]
    range,      \* [Writers -> <<from, to>>]: the range the writer takes
    held,       \* [Writers -> the version the writer holds]
    base,       \* [Writers -> the version the attempt builds on]
    listed,     \* [Writers -> whether that version is the latest the store said, rather than one held]
    verdict,    \* [Writers -> the verdict on the version the writer prepared on, which the mutation keeps]
    attempts,   \* [Writers -> attempts lost]
    commits,    \* how many commits were prepared, which tags their rows
    tag,        \* [Writers -> the tag of the rows the writer's commit carries]
    conflicts,  \* witness: writers told their range conflicts though it started at the latest offset
    counted     \* witness: writers told their range was counted already

vars == <<history, pc, range, held, base, listed, verdict, attempts, commits, tag, conflicts, counted>>
Ghosts == <<conflicts, counted>>

Latest == Len(history)

Init == /\ history = <<[offset |-> 0, rows |-> {}]>>
        /\ pc = [w \in Writers |-> "take"]
        /\ range = [w \in Writers |-> <<0, 0>>]
        /\ held = [w \in Writers |-> 1]
        /\ base = [w \in Writers |-> 1]
        /\ listed = [w \in Writers |-> TRUE]
        /\ verdict = [w \in Writers |-> "none"]
        /\ attempts = [w \in Writers |-> 0]
        /\ commits = 0
        /\ tag = [w \in Writers |-> 0]
        /\ conflicts = {}
        /\ counted = {}

\* SourceTable.Judge: the range against the offset of a version.
Judge(v, r) == IF history[v].offset = r[1] THEN "Applied"
               ELSE IF history[v].offset >= r[2] THEN "Counted"
               ELSE "Conflict"

\* A writer holds a version it may have read long ago, a handle refreshed long ago or opened by its
\* number, and takes the next entries from where its source says it stands: the offset some version
\* reached, which may be past the one it holds.
Take(w) ==
    /\ pc[w] = "take"
    /\ \E v \in 1..Latest, u \in 1..Latest, to \in 1..Entries :
         LET from == history[u].offset IN
         /\ from < to
         /\ range' = [range EXCEPT ![w] = <<from, to>>]
         /\ held' = [held EXCEPT ![w] = v]
         /\ verdict' = [verdict EXCEPT ![w] = Judge(v, <<from, to>>)]
    /\ commits' = commits + 1
    /\ tag' = [tag EXCEPT ![w] = commits + 1]
    /\ pc' = [pc EXCEPT ![w] = "attempt"]
    /\ UNCHANGED <<history, base, listed, attempts, Ghosts>>

\* The version an attempt builds on: the one held, taken outright, or the latest the store lists.
Attempt(w) ==
    /\ pc[w] = "attempt"
    /\ \/ base' = [base EXCEPT ![w] = held[w]] /\ listed' = [listed EXCEPT ![w] = (held[w] = Latest)]
       \/ base' = [base EXCEPT ![w] = Latest] /\ listed' = [listed EXCEPT ![w] = TRUE]
    /\ pc' = [pc EXCEPT ![w] = "put"]
    /\ UNCHANGED <<history, range, held, verdict, attempts, commits, tag, Ghosts>>

\* The verdict on the version built on, then the conditional creation of the next version.
Put(w) ==
    /\ pc[w] = "put"
    /\ LET v == base[w]
           seen == IF JudgeEachAttempt THEN Judge(v, range[w]) ELSE verdict[w]
           rows == {<<tag[w], e>> : e \in (range[w][1] + 1)..range[w][2]}
       IN IF seen # "Applied" /\ AskBeforeVerdict /\ ~listed[w]
          THEN \* Decided on a version held: asked again on the latest (AttemptAsync, askFirst).
               /\ base' = [base EXCEPT ![w] = Latest]
               /\ listed' = [listed EXCEPT ![w] = TRUE]
               /\ UNCHANGED <<history, pc, attempts, held, commits, tag, Ghosts>>
          ELSE IF seen = "Counted"
          THEN /\ pc' = [pc EXCEPT ![w] = "done"]
               /\ counted' = counted \cup {w}
               /\ UNCHANGED <<history, base, listed, attempts, held, commits, tag, conflicts>>
          ELSE IF seen = "Conflict"
          THEN /\ pc' = [pc EXCEPT ![w] = "done"]
               /\ conflicts' = IF history[Latest].offset = range[w][1] THEN conflicts \cup {w} ELSE conflicts
               /\ UNCHANGED <<history, base, listed, attempts, held, commits, tag, counted>>
          ELSE IF Latest = v
          THEN /\ history' = Append(history, [offset |-> range[w][2], rows |-> history[v].rows \cup rows])
               /\ \/ /\ pc' = [pc EXCEPT ![w] = "done"]
                     /\ UNCHANGED <<commits, tag>>
                  \/ /\ Crashes
                     \* The put landed and the writer never learned it: started again, it takes the
                     \* same range in a new commit of new rows.
                     /\ pc' = [pc EXCEPT ![w] = "attempt"]
                     /\ commits' = commits + 1
                     /\ tag' = [tag EXCEPT ![w] = commits + 1]
               /\ UNCHANGED <<base, listed, attempts, held, Ghosts>>
          ELSE \* Another writer created the version: rebase, judged again on the version that won.
               /\ attempts' = [attempts EXCEPT ![w] = @ + 1]
               /\ pc' = [pc EXCEPT ![w] = IF attempts[w] + 1 < MaxAttempts THEN "attempt" ELSE "done"]
               /\ held' = [held EXCEPT ![w] = Latest]
               /\ UNCHANGED <<history, base, listed, commits, tag, Ghosts>>
    /\ UNCHANGED <<range, verdict>>

\* A writer that finds the source taken up to its end in every version takes nothing.
Rest(w) == /\ pc[w] = "take"
           /\ \A v \in 1..Latest : history[v].offset >= Entries
           /\ pc' = [pc EXCEPT ![w] = "done"]
           /\ UNCHANGED <<history, range, held, base, listed, verdict, attempts, commits, tag, Ghosts>>

Next == \E w \in Writers : Take(w) \/ Attempt(w) \/ Put(w) \/ Rest(w)

Spec == Init /\ [][Next]_vars

\* How many rows of a version hold entry e.
Copies(v, e) == Cardinality({r \in history[v].rows : r[2] = e})

\* Every entry up to the offset is counted once, and none past it.
ExactlyOnce == \A e \in 1..Entries :
                 Copies(Latest, e) = IF e <= history[Latest].offset THEN 1 ELSE 0

\* A writer whose range starts at the latest offset is never told it conflicts.
NoSpuriousConflict == conflicts = {}

\* Witness: a writer is told its range was counted already.
NoReplay == counted = {}
================================================================================
