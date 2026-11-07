---------------------------- MODULE ConditionalPut ----------------------------
(***************************************************************************)
(* A put-if-absent over HTTP, as the stores of Vorticity.ObjectStorage    *)
(* make it (src/Vorticity.ObjectStorage/Http/Upload.cs: SettleAsync,      *)
(* LearnAsync, RecheckAsync). Every attempt carries the put's random id in *)
(* the object's metadata. When an attempt's answer is lost, a head says    *)
(* whose object the key holds. A refusal that follows a lost answer is     *)
(* checked the same way, since the lost attempt may be the one that took   *)
(* the key.                                                                 *)
(*                                                                          *)
(* The service applies a request whenever it likes: before its answer is  *)
(* lost, after, and after the head that followed, which                    *)
(* docs/design/19-object-storage.md allows ("A slow service may apply the  *)
(* lost attempt after the head that found nothing"). It may also drop a    *)
(* request without applying it.                                            *)
(***************************************************************************)
EXTENDS Naturals, FiniteSets

CONSTANTS
    Writers,        \* the puts racing for the key, each with its own put id
    MaxTries,       \* the attempts the store's retry budget allows
    GiveUpAs,       \* what a put answers when the budget ends after lost answers
    RecheckRefusal  \* whether a refusal after a lost answer is checked by a head

ASSUME MaxTries \in Nat \ {0}
ASSUME GiveUpAs \in {"failed", "unknown"}
ASSUME RecheckRefusal \in BOOLEAN

None == "none"

VARIABLES
    obj,        \* the put id of the object under the key, or None
    inflight,   \* the requests the service holds and has not applied: <<writer, attempt>>
    verdict,    \* [Writers -> [1..MaxTries -> {"pending", "created", "refused", "dropped"}]]
    pc,         \* [Writers -> {"send", "wait", "head", "recheck", "done"}]
    tries,      \* [Writers -> 0..MaxTries]: the attempts sent
    ambiguous,  \* [Writers -> BOOLEAN]: an answer of this put was lost
    outcome,    \* [Writers -> {"pending", "created", "exists", "failed", "unknown"}]
    freeSeen,   \* [Writers -> SUBSET 1..MaxTries]: attempts pending when a head found the key free
    lateApply   \* witness: an attempt was applied after a head had found the key free

vars == <<obj, inflight, verdict, pc, tries, ambiguous, outcome, freeSeen, lateApply>>

Init == /\ obj = None
        /\ inflight = {}
        /\ verdict = [w \in Writers |-> [i \in 1..MaxTries |-> "pending"]]
        /\ pc = [w \in Writers |-> "send"]
        /\ tries = [w \in Writers |-> 0]
        /\ ambiguous = [w \in Writers |-> FALSE]
        /\ outcome = [w \in Writers |-> "pending"]
        /\ freeSeen = [w \in Writers |-> {}]
        /\ lateApply = FALSE

Decide(w, o) == /\ outcome' = [outcome EXCEPT ![w] = o]
                /\ pc' = [pc EXCEPT ![w] = "done"]

\* An attempt, sent with the put's id (Upload.cs, PutWholeAsync or the completion of parts).
Send(w) == /\ pc[w] = "send"
           /\ tries[w] < MaxTries
           /\ tries' = [tries EXCEPT ![w] = @ + 1]
           /\ inflight' = inflight \cup {<<w, tries[w] + 1>>}
           /\ pc' = [pc EXCEPT ![w] = "wait"]
           /\ UNCHANGED <<obj, verdict, ambiguous, outcome, freeSeen, lateApply>>

\* The service applies a request it holds: it creates the object on a free key, refuses otherwise.
Apply(r) == LET w == r[1]
                i == r[2]
            IN /\ inflight' = inflight \ {r}
               /\ verdict' = [verdict EXCEPT ![w][i] = IF obj = None THEN "created" ELSE "refused"]
               /\ obj' = IF obj = None THEN w ELSE obj
               /\ lateApply' = (lateApply \/ (obj = None /\ i \in freeSeen[w]))
               /\ UNCHANGED <<pc, tries, ambiguous, outcome, freeSeen>>

\* The service drops a request without applying it.
Drop(r) == /\ inflight' = inflight \ {r}
           /\ verdict' = [verdict EXCEPT ![r[1]][r[2]] = "dropped"]
           /\ UNCHANGED <<obj, pc, tries, ambiguous, outcome, freeSeen, lateApply>>

\* The answer of the attempt in flight arrives. A refusal after a lost answer goes to a head
\* (Upload.cs:513-514), unless the mutation takes it as it is.
Answer(w) == LET v == verdict[w][tries[w]] IN
             /\ pc[w] = "wait"
             /\ v \in {"created", "refused"}
             /\ IF v = "created"
                THEN Decide(w, "created")
                ELSE IF ambiguous[w] /\ RecheckRefusal
                     THEN pc' = [pc EXCEPT ![w] = "recheck"] /\ UNCHANGED outcome
                     ELSE Decide(w, "exists")
             /\ UNCHANGED <<obj, inflight, verdict, tries, ambiguous, freeSeen, lateApply>>

\* The answer is lost, the request applied or still held (StoreAnswerLostException).
Lost(w) == /\ pc[w] = "wait"
           /\ pc' = [pc EXCEPT ![w] = "head"]
           /\ ambiguous' = [ambiguous EXCEPT ![w] = TRUE]
           /\ UNCHANGED <<obj, inflight, verdict, tries, outcome, freeSeen, lateApply>>

\* LearnAsync (Upload.cs:521-556): the head says whose object the key holds. A free key sends the
\* put again while the budget allows (:505), and gives up past it (:507).
Head(w) == /\ pc[w] = "head"
           /\ LET pending == {i \in 1..tries[w] : verdict[w][i] = "pending"} IN
              CASE obj = w ->
                     Decide(w, "created") /\ UNCHANGED freeSeen
                [] obj # None ->
                     Decide(w, "exists") /\ UNCHANGED freeSeen
                [] tries[w] < MaxTries ->
                     /\ pc' = [pc EXCEPT ![w] = "send"]
                     /\ freeSeen' = [freeSeen EXCEPT ![w] = @ \cup pending]
                     /\ UNCHANGED outcome
                [] OTHER ->
                     /\ Decide(w, GiveUpAs)
                     /\ freeSeen' = [freeSeen EXCEPT ![w] = @ \cup pending]
           /\ UNCHANGED <<obj, inflight, verdict, tries, ambiguous, lateApply>>

\* RecheckAsync (Upload.cs:563-581): a refusal after a lost answer may be this put's own attempt.
Recheck(w) == /\ pc[w] = "recheck"
              /\ Decide(w, IF obj = w THEN "created" ELSE "exists")
              /\ UNCHANGED <<obj, inflight, verdict, tries, ambiguous, freeSeen, lateApply>>

\* The head fails too: the store says the outcome is unknown (OutcomeUnknown, :535 and :576).
HeadFails(w) == /\ pc[w] \in {"head", "recheck"}
                /\ Decide(w, "unknown")
                /\ UNCHANGED <<obj, inflight, verdict, tries, ambiguous, freeSeen, lateApply>>

Next == \/ \E w \in Writers : Send(w) \/ Answer(w) \/ Lost(w) \/ Head(w) \/ Recheck(w) \/ HeadFails(w)
        \/ \E r \in inflight : Apply(r) \/ Drop(r)

Spec == Init /\ [][Next]_vars

Fairness == \A w \in Writers : WF_vars(Send(w) \/ Answer(w) \/ Lost(w) \/ Head(w) \/ Recheck(w))

FairSpec == Spec /\ Fairness

TypeOK == /\ obj \in Writers \cup {None}
          /\ inflight \subseteq Writers \X (1..MaxTries)
          /\ pc \in [Writers -> {"send", "wait", "head", "recheck", "done"}]
          /\ outcome \in [Writers -> {"pending", "created", "exists", "failed", "unknown"}]

\* What each answer says holds when it is given, and stays true after: "created" means the key holds
\* this put's object, "exists" another put's, and "failed" that this put's object never lands.
Truthful == \A w \in Writers :
              /\ outcome[w] = "created" => obj = w
              /\ outcome[w] = "exists" => obj \notin {None, w}
              /\ outcome[w] = "failed" => obj # w /\ \A i \in 1..MaxTries : <<w, i>> \notin inflight

\* Every put ends with an answer.
EveryPutEnds == \A w \in Writers : <>(pc[w] = "done")

\* The store, seen through the contract it owes: the key's object is created once and never replaced.
Store == INSTANCE AtomicPut WITH Keys <- {"key"}, Puts <- Writers, Removable <- FALSE,
                                 slots <- [k \in {"key"} |-> obj]
StoreSafety == Store!SafetySpec

\* Witness: an attempt can land after the head that found the key free, the race the budget must
\* answer as unknown rather than failed.
NoLateApply == ~lateApply
=============================================================================
