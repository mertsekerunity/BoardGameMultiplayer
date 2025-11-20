using Mirror;
using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

public class TurnManager : NetworkBehaviour
{
    public static TurnManager Instance { get; private set; }

    public enum TurnActionType
    {
        None,
        Buy,
        Sell,
        Ability
    }

    public class TurnHistoryEntry
    {
        public TurnActionType type;
        public StockType stock;
        public bool openSale; // only for sells
        public CharacterAbilityType ability;
        public Action undo;
        public bool undoable = true;
        public int unitPrice;
    }

    private const int DefaultBuyLimit = 2;
    private const int DefaultSellLimit = 3;

    private int _buyLimitThisTurn = DefaultBuyLimit;
    private int _sellLimitThisTurn = DefaultSellLimit;

    private int _buyDeltaThisTurn = 0;   // Trader: -1
    private int _sellDeltaThisTurn = 0;  // Trader: +1

    private Coroutine _mainPhaseCo;
    private bool _turnWaiting = false;

    private Coroutine _biddingPhaseCo;
    private bool _biddingWaiting = false;

    private Coroutine _selectionCo;
    private bool _selectionWaiting;
    public event Action SelectionFinished;

    private int _selectionCurrentPid = -1;
    private List<CharacterCardSO> _selectionOptions;

    private int _biddingCurrentPid = -1;
    private readonly Dictionary<int, int> _playerBids = new(); // pid -> amount

    public event Action<int, ManipulationType, StockType> OnManipulationQueuedUI;
    public event Action<int, ManipulationType, StockType> OnManipulationRemovedUI;
    public event Action<int, StockType> OnProtectionChosenUI;

    // Blocked character numbers this round
    private readonly HashSet<int> _blockedCharacters = new();
    private readonly HashSet<int> _stolenCharacters = new();

    // Thief payouts to execute at end of the thief's turn
    private readonly List<(int thiefPid, int victimPid, int amount)> _pendingThief = new();

    // Tax collections to execute at end of round: (collectorPid, stock)
    private readonly List<(int collectorPid, StockType stock)> _pendingTaxes = new();

    // per-turn state
    private TurnActionType _turnAction = TurnActionType.None;
    private int _buyUsed = 0;
    private int _sellUsed = 0;

    // Ability for this player’s turn
    private CharacterAbilityType _activeAbility;
    private bool _abilityAvailable = false;

    private readonly HashSet<StockType> _manipulatedStocksThisRound = new();
    private readonly List<PendingManipulation> _pendingManipulations = new();

    private readonly Dictionary<int, List<ManipulationType>> _cachedManipOptions = new(); // for Manipulator (3 choices)
    private readonly Dictionary<int, ManipulationType> _cachedSingleManip = new();        // for Lottery/Broker (1 choice)

    // first price per stock (bulk advantage anchors)
    private readonly Dictionary<StockType, int> _lockedBuyPrice = new();
    private readonly Dictionary<StockType, int> _lockedSellPrice = new();

    private readonly Stack<TurnHistoryEntry> _history = new();

    public bool CanUndoCurrentTurn => (ActivePlayerId >= 0) && (_history.Count > 0);

    public List<CharacterCardSO> characterDeck;

    private int maxCharacters => characterDeck.Count;

    public int ActivePlayerId { get; private set; } = -1;

    // Round state
    private List<CharacterCardSO> discardDeck;
    public List<CharacterCardSO> faceUpDiscards { get; private set; } = new List<CharacterCardSO>();
    public List<CharacterCardSO> faceDownDiscards { get; private set; } = new List<CharacterCardSO>();
    public List<CharacterCardSO> availableCharacters { get; private set; } = new List<CharacterCardSO>();

    public int BiddingCurrentPid => _biddingCurrentPid;

    public List<int> biddingOrder { get; private set; }
    public List<int> selectionOrder { get; private set; }
    public Dictionary<CharacterCardSO, int> characterAssignments { get; private set; }

    private readonly Dictionary<int, int> _bidSpendTotal = new();
    public int GetTotalBidSpend(int pid) => _bidSpendTotal.TryGetValue(pid, out var s) ? s : 0;

    private readonly HashSet<StockType> _protectedStocksThisRound = new();

    public bool IsStockProtected(StockType s) => _protectedStocksThisRound.Contains(s);

    private struct PendingManipulation
    {
        public int playerId;
        public ManipulationType card;
        public StockType stock;
    }

    public event Action BiddingFinished;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        //DontDestroyOnLoad(transform.root.gameObject);
    }

    [Server]
    public void SetupTurnOrder()
    {
        //Determine first-round random order or bidding order

        selectionOrder = PlayerManager.Instance.players.Select(p => p.id).OrderBy(_ => UnityEngine.Random.value).ToList(); //LINQ 
    }

    [Server]
    public void StartDiscardPhase()
    {
        Debug.Log("Discard Phase started.");

        int playerCount = PlayerManager.Instance.players.Count;
        int discardCount = maxCharacters - (playerCount + 1);

        // Determine face-up and face-down counts
        int faceUpCount = (playerCount <= 4) ? 2 : 1; // ?: operator - the ternary conditional operator
        faceUpCount = Mathf.Clamp(faceUpCount, 1, discardCount);
        int faceDownCount = discardCount - faceUpCount;

        // Prepare and shuffle deck
        discardDeck = new List<CharacterCardSO>(characterDeck);
        Shuffle(discardDeck);

        // Assign discards
        faceUpDiscards = discardDeck.Take(faceUpCount).ToList();
        faceDownDiscards = discardDeck.Skip(faceUpCount).Take(faceDownCount).ToList();
        availableCharacters = discardDeck.Skip(discardCount).ToList();

        //UIManager.Instance.ShowFaceUpDiscards(faceUpDiscards); // no UI call directly in server-side

        int[] faceUpIds = faceUpDiscards.Select(c => (int)c.characterNumber).ToArray();

        RpcShowFaceUpDiscards(faceUpIds);
    }

    [Server]
    public void StartBiddingPhase()
    {
        Debug.Log("Bidding Phase started.");

        biddingOrder = PlayerManager.Instance.players.OrderBy(p => p.money).Select(p => p.id).ToList();

        // tell all clients to reset their bidding UI
        RpcBiddingReset(PlayerManager.Instance.players.Count);

        if (_biddingPhaseCo != null)
        {
            StopCoroutine(_biddingPhaseCo);
        }

        _biddingPhaseCo = StartCoroutine(BiddingPhaseLoop());
    }

    [Server]
    private IEnumerator BiddingPhaseLoop()
    {
        foreach (int pid in biddingOrder)
        {
            _biddingCurrentPid = pid;
            _biddingWaiting = true;

            // everyone highlights / sees whose turn it is
            RpcSetBidActivePlayer(pid);

            // find that player's NetPlayer on the *server*
            var p = PlayerManager.Instance.players.First(pp => pp.id == pid);
            var nm = NetworkManager.singleton as CustomNetworkManager;
            var netPlayer = nm?.GetPlayerByPid(pid);

            if (netPlayer != null)
            {
                // only that client's UI becomes interactive
                netPlayer.TargetBeginBidTurn(p.playerName, p.money);
            }
            else
            {
                Debug.LogError($"[BIDDING] No NetPlayer for pid={pid}");
            }

            // Wait for that player to pick a circle (SubmitBid_Server sets _biddingWaiting=false)
            while (_biddingWaiting)
                yield return null;
        }

        // clear highlights on all clients
        RpcClearAllHighlights();

        // Compute selection order
        selectionOrder = _playerBids
            .OrderByDescending(kv => kv.Value)
            .ThenBy(_ => UnityEngine.Random.value)
            .Select(kv => kv.Key)
            .ToList();

        // hide panel on all clients
        RpcBiddingClose();
        BiddingFinished?.Invoke();

        Debug.Log("[BIDDING] Final order: " + string.Join(", ", selectionOrder.Select(id => $"P{id}")));
    }

    [Server]
    public void StartCharacterSelectionPhase()
    {
        Debug.Log("Character Selection Phase started.");

        // Don’t run two selection loops
        if (_selectionCo != null) StopCoroutine(_selectionCo);
        _selectionCo = StartCoroutine(SelectionPhaseLoop());
    }

    [Server]
    private IEnumerator SelectionPhaseLoop()
    {
        characterAssignments = new Dictionary<CharacterCardSO, int>();

        _selectionOptions = new List<CharacterCardSO>(availableCharacters);

        var selectionOrderCopy = selectionOrder.ToList();

        foreach (int pid in selectionOrderCopy)
        {
            _selectionWaiting = true;
            _selectionCurrentPid = pid;

            int[] optionIds = _selectionOptions.Select(c => (int)c.characterNumber).ToArray();

            RpcShowCharacterSelection(pid, optionIds);

            while (_selectionWaiting)
                yield return null;
        }

        _selectionCurrentPid = -1;
        SelectionFinished?.Invoke();
    }

    [Server]
    public void StartMainPhase()
    {
        Debug.Log("Main Phase started.");

        if (_mainPhaseCo != null)
        {
            StopCoroutine(_mainPhaseCo);
        }
     
        _mainPhaseCo = StartCoroutine(MainPhaseLoop());       
    }

    [Server]
    private IEnumerator MainPhaseLoop()
    {
        var selectedCards = characterAssignments.Keys.OrderBy(c => c.characterNumber).ToList();

        foreach (var card in selectedCards)
        {
            // Start this player's turn
            HandleCharacterTurn(card);   // sets ActivePlayerId, enables UI, etc.

            _turnWaiting = true;
            while (_turnWaiting)
            {
                yield return null;       // one frame at a time
            }
        }

        ResolveEndOfRound();

        // Main phase finished -> end round (or next phase)
        GameManager.Instance.EndRound();
    }

    [Server]
    private void HandleCharacterTurn(CharacterCardSO card)
    {
        if (!characterAssignments.ContainsKey(card)) return;
        int pid = characterAssignments[card];
        ActivePlayerId = pid;

        int cardId = (int)card.characterNumber;

        RpcShowActiveCharacter(pid, cardId);

        BeginActivePlayerTurn(pid, card.abilityType);
    }

    [ClientRpc]
    private void RpcSetActivePlayer(int pid, bool enable)
    {
        UIManager.Instance.SetActivePlayer(pid, enable);
    }

    [ClientRpc]
    private void RpcBiddingReset(int playerCount)
    {
        UIManager.Instance.Bidding_Reset(playerCount);
    }

    [ClientRpc]
    private void RpcSetBidActivePlayer(int pid)
    {
        UIManager.Instance.SetBidActivePlayer(pid);
    }

    [ClientRpc]
    private void RpcBiddingClose()
    {
        UIManager.Instance.Bidding_Close();
    }

    [ClientRpc]
    private void RpcClearAllHighlights()
    {
        UIManager.Instance.ClearAllHighlights();
    }

    [ClientRpc]
    private void RpcShowFaceUpDiscards(int[] faceUpIds)
    {
        if (UIManager.Instance == null)
            return;

        var list = new List<CharacterCardSO>();

        foreach (int id in faceUpIds)
        {
            var card = characterDeck.FirstOrDefault(c => (int)c.characterNumber == id);
            if (card != null)
                list.Add(card);
            else
                Debug.LogWarning($"[DISCARD] Could not find character with id={id} in characterDeck.");
        }

        Debug.Log($"[DISCARD] ShowFaceUpDiscards on client, count={list.Count}");
        UIManager.Instance.ShowFaceUpDiscards(list);
    }

    [ClientRpc]
    private void RpcShowActiveCharacter(int pid, int cardId)
    {
        if (UIManager.Instance == null)
            return;

        var card = characterDeck.FirstOrDefault(c => (int)c.characterNumber == cardId);
        if (card == null)
        {
            Debug.LogWarning($"[TURN] RpcShowActiveCharacter: could not find card id={cardId} in characterDeck");
            return;
        }

        UIManager.Instance.ShowCharacter(card, pid);
    }

    [ClientRpc]
    private void RpcHideActiveCharacter(int pid, int cardId)
    {
        if (UIManager.Instance == null)
            return;

        var card = characterDeck.FirstOrDefault(c => (int)c.characterNumber == cardId);
        if (card == null)
        {
            Debug.LogWarning($"[TURN] RpcHideActiveCharacter: could not find card id={cardId} in characterDeck");
            // HideCharacter kartı kullanmıyorsa sadece paneli kapatmak için
            // overload varsa ona göre çağırabilirsin.
            return;
        }

        UIManager.Instance.HideCharacter(card, pid);
    }

    [Server]
    public void BeginActivePlayerTurn(int pid, CharacterAbilityType ability)
    {
        ActivePlayerId = pid;
        _turnAction = TurnActionType.None;
        _buyLimitThisTurn = DefaultBuyLimit;
        _sellLimitThisTurn = DefaultSellLimit;
        _buyUsed = 0;
        _sellUsed = 0;
        _buyDeltaThisTurn = 0;
        _sellDeltaThisTurn = 0;
        _lockedBuyPrice.Clear();
        _lockedSellPrice.Clear();
        _history.Clear();

        _activeAbility = ability;
        _abilityAvailable = true;

        RpcSetActivePlayer(pid, true);
    }

    [Server]
    public void EndActivePlayerTurn()
    {
        if (ActivePlayerId < 0) return;

        var card = PlayerManager.Instance.players[ActivePlayerId].selectedCard;

        int cardId = (int)card.characterNumber;
        RpcHideActiveCharacter(ActivePlayerId, cardId);

        // if the active character was Thief, pay now
        ResolveThiefPayoutsFor(ActivePlayerId);

        _history.Clear(); // can’t undo previous player’s actions

        // Disable interactivity for this player on all clients
        RpcSetActivePlayer(ActivePlayerId, false);
        RpcClearAllHighlights();

        ActivePlayerId = -1;

        // Release the coroutine to continue to the next character
        _turnWaiting = false;
    }

    [Server]
    public void RoundStartReset()
    {
        UIManager.Instance.OnRoundStartUIReset();
        UIManager.Instance.HideAllUndoButtons();
        UIManager.Instance.HideFaceUpDiscards();
        _playerBids.Clear();
        _blockedCharacters.Clear();
        _stolenCharacters.Clear();
        _pendingThief.Clear();
        _pendingTaxes.Clear();
        _manipulatedStocksThisRound.Clear();
        _pendingManipulations.Clear();
        _protectedStocksThisRound.Clear();
        faceUpDiscards.Clear();
        faceDownDiscards.Clear();

        foreach (var kv in _cachedManipOptions)
            foreach (var m in kv.Value)
                DeckManager.Instance.ReturnManipulationToDeck(m);
        _cachedManipOptions.Clear();

        foreach (var kv in _cachedSingleManip)
            DeckManager.Instance.ReturnManipulationToDeck(kv.Value);
        _cachedSingleManip.Clear();
    }

    [Server]
    private void PushAbilityBarrier(Action undo)
    {
        _abilityAvailable = false;
        UIManager.Instance.SetAbilityButtonState(false);

        _history.Push(new TurnHistoryEntry
        {
            type = TurnActionType.Ability,
            ability = _activeAbility,
            undo = () =>
            {
                // kept for completeness in case you allow some abilities to be undoable later
                undo?.Invoke();
                _abilityAvailable = true;
                UIManager.Instance.SetAbilityButtonState(true);
            },
            undoable = false // <<< barrier
        });

        // Top is a barrier → no undo available until player does a new Buy/Sell
        UIManager.Instance.SetUndoButtonInteractable(false);
    }

    [Server]
    public void RaiseLimitsForThisTurn(int buyLimit, int sellLimit)
    {
        _buyLimitThisTurn = buyLimit;
        _sellLimitThisTurn = sellLimit;
        UIManager.Instance.ShowMessage($"This turn limits: buy {_buyLimitThisTurn}, sell {_sellLimitThisTurn}");
    }

    [Server]
    private int GetAnchoredBuyPrice(StockType stock)
    {
        if (!_lockedBuyPrice.TryGetValue(stock, out var anchor))
        {
            anchor = StockMarketManager.Instance.stockPrices[stock];
            _lockedBuyPrice[stock] = anchor; // anchor first time you buy this stock this turn
        }
        // <-- your line goes here
        int payPrice = Mathf.Max(0, anchor + _buyDeltaThisTurn); // Trader: -1
        return payPrice;
    }

    [Server]
    private int GetAnchoredSellPrice(StockType stock)
    {
        if (!_lockedSellPrice.TryGetValue(stock, out var anchor))
        {
            anchor = StockMarketManager.Instance.stockPrices[stock];
            _lockedSellPrice[stock] = anchor; // anchor first time you sell this stock this turn
        }
        // <-- your line goes here
        int gainPrice = Mathf.Max(0, anchor + _sellDeltaThisTurn); // Trader: +1
        return gainPrice;
    }

    [Server]
    public bool TryBuyOne(StockType stock)
    {
        Debug.Log($"[TURN] BUY {_turnAction} used={_buyUsed},{_sellUsed}"); // REMOVE LATER !!!

        if (ActivePlayerId < 0) return false;

        // lock action type: only "buy" this turn
        if (_turnAction == TurnActionType.None) _turnAction = TurnActionType.Buy;
        if (_turnAction != TurnActionType.Buy) return false;

        // per-turn limit
        if (_buyUsed >= _buyLimitThisTurn) return false;

        // anchor the first time we buy THIS stock this turn
        int current = StockMarketManager.Instance.stockPrices[stock];
        if (!_lockedBuyPrice.ContainsKey(stock))
            _lockedBuyPrice[stock] = current;

        // anchored pay price with Trader delta (e.g., -1)
        int payPrice = GetAnchoredBuyPrice(stock);

        // must afford the anchored price (not the current)
        var p = PlayerManager.Instance.players.First(pp => pp.id == ActivePlayerId);
        if (p.money < payPrice) return false;

        // Do the actual buy at CURRENT price; market will tick up
        bool ok = PlayerManager.Instance.TryBuyStock(ActivePlayerId, stock);
        if (!ok) return false;

        // Refund the difference so net paid == anchored (bulk pricing / Trader)
        int refunded = current - payPrice;       // current >= payPrice normally
        if (refunded > 0) PlayerManager.Instance.AddMoney(ActivePlayerId, refunded);

        _history.Push(new TurnHistoryEntry { type = TurnActionType.Buy, stock = stock });
        _buyUsed++;
        UIManager.Instance.SetUndoButtonInteractable(true);
        return true;
    }

    [Server]
    public bool TrySellOne(StockType stock, bool openSale)
    {
        Debug.Log($"[TURN] SELL {_turnAction} used={_buyUsed},{_sellUsed}");

        if (ActivePlayerId < 0) return false;

        // lock action type: only "sell" this turn
        if (_turnAction == TurnActionType.None) _turnAction = TurnActionType.Sell;
        if (_turnAction != TurnActionType.Sell) return false;

        // per-turn limit
        if (_sellUsed >= _sellLimitThisTurn) return false;

        // anchor the first time we sell THIS stock this turn
        int current = StockMarketManager.Instance.stockPrices[stock];
        if (!_lockedSellPrice.ContainsKey(stock))
            _lockedSellPrice[stock] = current;

        // anchored gain with Trader delta (e.g., +1)
        int anchoredGain = GetAnchoredSellPrice(stock);

        if (openSale)
        {
            // Immediate sale: execute now
            bool ok = PlayerManager.Instance.TrySellStock(ActivePlayerId, stock, openSale: true);
            if (!ok) return false;

            // Player received 'current'; top up so net == anchored
            int topUp = anchoredGain - current;
            if (topUp > 0) PlayerManager.Instance.AddMoney(ActivePlayerId, topUp);
        }
        else
        {
            // CLOSE SALE: verify availability considering existing pendings
            var p = PlayerManager.Instance.players.First(pp => pp.id == ActivePlayerId);
            int owned = p.stocks.TryGetValue(stock, out var cnt) ? cnt : 0;
            int pending = PlayerManager.Instance.GetPendingCloseCount(ActivePlayerId, stock);
            if ((owned - pending) <= 0) return false;

            // Mark pending in PM (no card removal, no money now)
            // IMPORTANT: PlayerManager.TrySellStock(..., false) should NOT queue to market or remove a card.
            bool ok = PlayerManager.Instance.TrySellStock(ActivePlayerId, stock, openSale: false);
            if (!ok) return false;

            // Queue market payout at the anchored price; price tick happens at end-of-round
            StockMarketManager.Instance.QueueCloseSale(ActivePlayerId, stock, anchoredGain);
        }

        // History (store unitPrice only for close-sale so Undo can remove exact queued item)
        _history.Push(new TurnHistoryEntry
        {
            type = TurnActionType.Sell,
            stock = stock,
            openSale = openSale,
            unitPrice = openSale ? 0 : anchoredGain
        });

        _sellUsed++;
        UIManager.Instance.SetUndoButtonInteractable(true);
        return true;
    }

    [Server]
    public bool TryUseAbility()
    {
        if (ActivePlayerId < 0 || !_abilityAvailable) return false;

        var myNum = GetMyCharacterNumber(ActivePlayerId);
        if (myNum.HasValue && IsCharacterBlocked(myNum.Value))
        {
            UIManager.Instance.ShowMessage("Your ability is blocked this round.");
            _abilityAvailable = false;
            UIManager.Instance.SetAbilityButtonState(false);

            // non-undoable barrier so Undo doesn’t cross this
            _history.Push(new TurnHistoryEntry { type = TurnActionType.Ability, undoable = false });
            UIManager.Instance.SetUndoButtonInteractable(false);
            return false;
        }

        if (_activeAbility == CharacterAbilityType.Manipulator)
        {
            var trio = GetOrCreateManipOptions(ActivePlayerId); // cached; same 3 shown on reopen
            var pName = PlayerManager.Instance.players[ActivePlayerId].playerName;

            UIManager.Instance.ShowManipulationChoice(
                ActivePlayerId,
                trio,
                $"{pName}, choose a manipulation",
                (chosen, _discardIgnored, _returnIgnored, cancelSentinel) =>
                {
                    if ((int)cancelSentinel == -999)
                    {
                        // Cancel: keep cached trio; don’t return to deck; don’t consume ability
                        UIManager.Instance.HidePrompt();
                        return;
                    }

                    // Build enabled targets (not already manipulated/protected)
                    var enabledStocks = new HashSet<StockType>(
                        StockMarketManager.Instance.availableStocks
                            .Where(s => !_manipulatedStocksThisRound.Contains(s) && !_protectedStocksThisRound.Contains(s))
                    );

                    if (enabledStocks.Count == 0)
                    {
                        UIManager.Instance.ShowMessage("No stocks available to manipulate.");
                        // Keep cached trio so they’ll see the same choices next time
                        return;
                    }

                    var manipPrompt = $"{pName}, choose a stock to apply manipulation";
                    UIManager.Instance.ShowStockTargetPanel(
                        ActivePlayerId,
                        enabledStocks,
                        manipPrompt,
                        onChosen: (applyStock) =>
                        {
                            if (!TryReserveSpecificManipulationTarget(applyStock))
                            {
                                UIManager.Instance.ShowMessage("That stock can’t be manipulated (already reserved).");
                                return;
                            }

                            // Queue chosen for end-of-round; now CONSUME the cached trio
                            var undoQueue = QueueManipulation(ActivePlayerId, chosen, applyStock);
                            ConsumeManipOptions(ActivePlayerId, chosen);

                            // Next: choose a stock to PROTECT (not the one just manipulated)
                            var protectCandidates = new HashSet<StockType>(
                                StockMarketManager.Instance.availableStocks
                                    .Where(s => s != applyStock && !_protectedStocksThisRound.Contains(s))
                            );

                            if (protectCandidates.Count == 0)
                            {
                                // No protection possible; still consume the ability
                                PushAbilityBarrier(() => { undoQueue?.Invoke(); });
                                UIManager.Instance.HidePrompt();
                                return;
                            }

                            var protectPrompt = $"{pName}, choose a stock to protect";
                            UIManager.Instance.ShowStockTargetPanel(
                                ActivePlayerId,
                                protectCandidates,
                                protectPrompt,
                                onChosen: (protectStock) =>
                                {
                                    if (!TryProtectStock(protectStock))
                                    {
                                        UIManager.Instance.ShowMessage("That stock is already protected.");
                                        // We still consumed the manipulation above
                                        PushAbilityBarrier(() => { undoQueue?.Invoke(); });
                                        UIManager.Instance.HidePrompt();
                                        return;
                                    }

                                    // Optional private “Protected” tag
                                    OnProtectionChosenUI?.Invoke(ActivePlayerId, protectStock);

                                    // Finalize ability (barrier; undo unprotect + unqueue)
                                    PushAbilityBarrier(() =>
                                    {
                                        UnprotectStock(protectStock);
                                        undoQueue?.Invoke();
                                    });
                                    UIManager.Instance.HidePrompt();
                                },
                                onCancelled: () =>
                                {
                                    // No protection picked; still consume ability (only manipulation queued)
                                    PushAbilityBarrier(() => { undoQueue?.Invoke(); });
                                    UIManager.Instance.HidePrompt();
                                }
                            );
                        },
                        onCancelled: () =>
                        {
                            // Cancel at stock-pick stage: keep the trio cached; ability remains available
                            UIManager.Instance.HidePrompt();
                        }
                    );
                }
            );

            return true; // handled; flow continues via callbacks
        }

        // --- Lottery Winner (#5): claim lottery + apply ONE random manipulation to ONE chosen stock ---
        if (_activeAbility == CharacterAbilityType.LotteryWinner)
        {
            int payout = DeckManager.Instance.ClaimLottery();
            PlayerManager.Instance.AddMoney(ActivePlayerId, payout);

            var m = GetOrCreateSingleManip(ActivePlayerId); // cached; same card on reopen

            UIManager.Instance.ShowPrivateManipPeek(ActivePlayerId, m);

            var enabled = new HashSet<StockType>(
                StockMarketManager.Instance.availableStocks
                    .Where(s => !_manipulatedStocksThisRound.Contains(s) && !_protectedStocksThisRound.Contains(s))
            );

            if (enabled.Count == 0)
            {
                UIManager.Instance.ShowMessage("No stocks available to manipulate.");
                // Consume ability now; set barrier with full revert for fairness (in case you later allow ability-undo)
                PushAbilityBarrier(() =>
                {
                    // Return cached card and revert payout
                    DeckManager.Instance.ReturnManipulationToDeck(m);
                    ConsumeSingleManip(ActivePlayerId); // just clears cache
                    DeckManager.Instance.RestoreLottery(payout);
                    PlayerManager.Instance.RemoveMoney(ActivePlayerId, payout);
                });
                return true;
            }

            string playerName = PlayerManager.Instance.players[ActivePlayerId].playerName;
            string prompt = $"{playerName}, choose a stock to apply manipulation";

            UIManager.Instance.ShowStockTargetPanel(
                ActivePlayerId,
                enabled,
                prompt,
                onChosen: (stock) =>
                {
                    UIManager.Instance.HidePrivateManipPeek();

                    if (!TryReserveSpecificManipulationTarget(stock))
                    {
                        UIManager.Instance.ShowMessage("That stock can’t be manipulated.");
                        // Revert and clear cache; ability not consumed yet (player can press again)
                        DeckManager.Instance.ReturnManipulationToDeck(m);
                        ConsumeSingleManip(ActivePlayerId);
                        DeckManager.Instance.RestoreLottery(payout);
                        PlayerManager.Instance.RemoveMoney(ActivePlayerId, payout);
                        UIManager.Instance.HidePrompt();
                        return;
                    }

                    var undoQueue = QueueManipulation(ActivePlayerId, m, stock);
                    ConsumeSingleManip(ActivePlayerId); // card committed

                    // Ability consumed → barrier; provides full revert semantics
                    PushAbilityBarrier(() =>
                    {
                        undoQueue?.Invoke(); // returns m; frees reservation
                        DeckManager.Instance.RestoreLottery(payout);
                        PlayerManager.Instance.RemoveMoney(ActivePlayerId, payout);
                    });
                    UIManager.Instance.HidePrompt();
                },
                onCancelled: () =>
                {
                    // Cancel: return cached card and revert payout; ability remains available
                    DeckManager.Instance.ReturnManipulationToDeck(m);
                    ConsumeSingleManip(ActivePlayerId);
                    DeckManager.Instance.RestoreLottery(payout);
                    PlayerManager.Instance.RemoveMoney(ActivePlayerId, payout);
                    UIManager.Instance.HidePrompt();
                }
            );

            return true;
        }

        // --- Broker (#6): +1 buy/sell limits this turn AND apply ONE random manipulation to ONE chosen stock ---
        if (_activeAbility == CharacterAbilityType.Broker)
        {
            RaiseLimitsForThisTurn(buyLimit: 3, sellLimit: 4);

            var m = GetOrCreateSingleManip(ActivePlayerId); // cached

            UIManager.Instance.ShowPrivateManipPeek(ActivePlayerId, m);

            var enabled = new HashSet<StockType>(
                StockMarketManager.Instance.availableStocks
                    .Where(s => !_manipulatedStocksThisRound.Contains(s) && !_protectedStocksThisRound.Contains(s))
            );

            string playerName = PlayerManager.Instance.players[ActivePlayerId].playerName;

            if (enabled.Count == 0)
            {
                UIManager.Instance.ShowMessage("No stocks available to manipulate.");
                // Still consume ability for the limits benefit
                PushAbilityBarrier(() =>
                {
                    RaiseLimitsForThisTurn(buyLimit: 2, sellLimit: 3);
                    DeckManager.Instance.ReturnManipulationToDeck(m);
                    ConsumeSingleManip(ActivePlayerId);
                });
                return true;
            }

            string prompt = $"{playerName}, choose a stock to apply manipulation";

            UIManager.Instance.ShowStockTargetPanel(
                ActivePlayerId,
                enabled,
                prompt,
                onChosen: (stock) =>
                {
                    UIManager.Instance.HidePrivateManipPeek();

                    if (!TryReserveSpecificManipulationTarget(stock))
                    {
                        UIManager.Instance.ShowMessage("That stock can’t be manipulated.");
                        // Revert limits and return cached card; ability not consumed
                        RaiseLimitsForThisTurn(buyLimit: 2, sellLimit: 3);
                        DeckManager.Instance.ReturnManipulationToDeck(m);
                        ConsumeSingleManip(ActivePlayerId);
                        UIManager.Instance.HidePrompt();
                        return;
                    }

                    var undoQueue = QueueManipulation(ActivePlayerId, m, stock);
                    ConsumeSingleManip(ActivePlayerId);
                    //UIManager.Instance.SetMarketSecretTagIfLocal(ActivePlayerId, stock, m);
                    //UIManager.Instance.HandleManipQueued(ActivePlayerId, m, stock);

                    // Ability consumed → barrier
                    PushAbilityBarrier(() =>
                    {
                        RaiseLimitsForThisTurn(buyLimit: 2, sellLimit: 3);
                        undoQueue?.Invoke();
                    });
                    UIManager.Instance.HidePrompt();
                },
                onCancelled: () =>
                {
                    // Cancel: revert limits and return cached card
                    RaiseLimitsForThisTurn(buyLimit: 2, sellLimit: 3);
                    DeckManager.Instance.ReturnManipulationToDeck(m);
                    ConsumeSingleManip(ActivePlayerId);
                    UIManager.Instance.HidePrompt();
                }
            );

            return true;
        }

        // Targeted abilities (Blocker, Thief): confirm before applying, then push barrier
        if (_activeAbility == CharacterAbilityType.Blocker ||
            _activeAbility == CharacterAbilityType.Thief)
        {
            string abiText = $"{PlayerManager.Instance.players[ActivePlayerId].playerName}, choose a character to use ability on";
            UIManager.Instance.ShowPrompt(abiText);

            RequestCharacterTarget(ActivePlayerId, _activeAbility, (chosenNum) =>
            {
                string label = _activeAbility.ToString();
                UIManager.Instance.ShowAbilityConfirm(
                    ActivePlayerId,
                    $"Use {label} on #{chosenNum}?",
                    onYes: () =>
                    {
                        var undo = PlayerManager.Instance
                            .ExecuteAbilityWithUndo_Targeted(ActivePlayerId, _activeAbility, chosenNum);

                        UIManager.Instance.HidePrompt();

                        if (undo == null)
                        {
                            Debug.Log("Ability had no effect."); // REMOVE LATER !!!
                            UIManager.Instance.ShowMessage("Ability had no effect.");
                            return;
                        }

                        PushAbilityBarrier(undo);
                    },
                    onNo: () => { /* do nothing; player can press Ability again */ }
                );
            });

            return true; // consumed the Ability button click
        }

        if (_activeAbility == CharacterAbilityType.TaxCollector)
        {
            var enabled = new HashSet<StockType>(StockMarketManager.Instance.availableStocks);
            if (enabled.Count == 0)
            {
                UIManager.Instance.ShowMessage("No stocks available for taxes.");
                return false;
            }

            string pName = PlayerManager.Instance.players[ActivePlayerId].playerName;

            UIManager.Instance.ShowStockTargetPanel(
                ActivePlayerId,
                enabled,
                $"{pName}, choose stock to apply taxes",
                onChosen: stock =>
                {
                    ScheduleTaxCollector(ActivePlayerId, stock);

                    // consume ability now (we keep it non-undoable like Blocker/Thief)
                    PushAbilityBarrier(undo: () =>
                    {
                        // if you ever allow undo for abilities:
                        UnscheduleTaxCollector(ActivePlayerId, stock);
                    });
                },
                onCancelled: () =>
                {
                    // do nothing; they can press Ability again later in the same turn
                });

            return true;
        }

        if (_activeAbility == CharacterAbilityType.Gambler)
        {
            int money = PlayerManager.Instance.players[ActivePlayerId].money;

            void TakeN(int n)
            {
                var taken = new List<StockType>();
                for (int i = 0; i < n; i++)
                {
                    var s = DeckManager.Instance.DrawRandomStock();
                    PlayerManager.Instance.AddStock(ActivePlayerId, s, 1);
                    taken.Add(s);
                }
                PlayerManager.Instance.RemoveMoney(ActivePlayerId, 3 * n);

                // consume ability, but keep non-undoable barrier
                PushAbilityBarrier(undo: () =>
                {
                    // if you later make abilities undoable, revert here:
                    PlayerManager.Instance.AddMoney(ActivePlayerId, 3 * n);
                    foreach (var s in taken)
                    {
                        PlayerManager.Instance.RemoveStock(ActivePlayerId, s, 1);
                        DeckManager.Instance.ReturnStockToRandom(s);
                    }
                });
            }

            if (money >= 6)
            {
                UIManager.Instance.ShowAbilityConfirm(
                    ActivePlayerId,
                    "Gamble: take 2 random stocks for 6$?",
                    onYes: () => TakeN(2),
                    onNo: () =>
                    {
                        if (money >= 3)
                        {
                            UIManager.Instance.ShowAbilityConfirm(
                                ActivePlayerId,
                                "Take 1 random stock for 3$?",
                                onYes: () => TakeN(1),
                                onNo: () => { /* cancelled, keep ability available */ }
                            );
                        }
                        else
                        {
                            UIManager.Instance.ShowMessage("Not enough money to gamble.");
                        }
                    });
            }
            else if (money >= 3)
            {
                UIManager.Instance.ShowAbilityConfirm(
                    ActivePlayerId,
                    "Gamble: take 1 random stock for 3$?",
                    onYes: () => TakeN(1),
                    onNo: () => { /* cancelled */ }
                );
            }
            else
            {
                UIManager.Instance.ShowMessage("Not enough money to gamble.");
            }

            return true;
        }

        // Non-targeted abilities (Gambler, Manipulator, Lottery, Broker, TaxCollector, Inheritor)
        var immediateUndo = PlayerManager.Instance.ExecuteAbilityWithUndo(ActivePlayerId, _activeAbility);
        if (immediateUndo == null)
        {
            UIManager.Instance.ShowMessage("Ability cannot be used now.");
            return false;
        }

        PushAbilityBarrier(immediateUndo);
        return true;
    }

    [Server]
    public bool TryReserveManipulationTarget(out StockType stock)
    {
        var pool = StockMarketManager.Instance.availableStocks
            .Where(s => !_manipulatedStocksThisRound.Contains(s) && !_protectedStocksThisRound.Contains(s))
            .ToList();

        if (pool.Count == 0)
        {
            stock = default;
            return false;
        }

        stock = pool[UnityEngine.Random.Range(0, pool.Count)];
        _manipulatedStocksThisRound.Add(stock);
        return true;
    }

    [Server]
    public bool TryReserveSpecificManipulationTarget(StockType stock)
    {
        if (_manipulatedStocksThisRound.Contains(stock)) return false;
        if (!StockMarketManager.Instance.availableStocks.Contains(stock)) return false;
        _manipulatedStocksThisRound.Add(stock);
        return true;
    }

    [Server]
    public void ReleaseManipulationTarget(StockType stock)
    {
        _manipulatedStocksThisRound.Remove(stock);
    }

    [Server]
    public Action QueueManipulation(int playerId, ManipulationType card, StockType stock)
    {
        OnManipulationQueuedUI?.Invoke(playerId, card, stock);

        var pm = new PendingManipulation { playerId = playerId, card = card, stock = stock };
        _pendingManipulations.Add(pm);

        // Undo: remove it from the queue and free the reservation, return the card to deck
        return () =>
        {
            int idx = _pendingManipulations.FindIndex(x => x.playerId == playerId && x.card == card && x.stock == stock);
            if (idx >= 0) _pendingManipulations.RemoveAt(idx);
            OnManipulationRemovedUI?.Invoke(playerId, card, stock);
            ReleaseManipulationTarget(stock);
            DeckManager.Instance.ReturnManipulationToDeck(card);
        };
    }

    [Server]
    public void ResolvePendingManipulationsEndOfRound()
    {
        if (_pendingManipulations.Count > 0)
        {
            Debug.Log("[Reveal] " + string.Join(", ", _pendingManipulations.Select(m => $"{m.card} on {m.stock}"))); // REMOVE LATER !!!
        }
            
        foreach (var pm in _pendingManipulations)
            ApplyManipulationNow(pm.card, pm.stock);

        _pendingManipulations.Clear();
        // reservations are irrelevant after reveal; will be cleared by RoundStartReset at next round
    }

    [Server]
    private void ApplyManipulationNow(ManipulationType m, StockType stock)
    {
        switch (m)
        {
            case ManipulationType.Plus1: StockMarketManager.Instance.AdjustPrice(stock, +1); break;
            case ManipulationType.Plus2: StockMarketManager.Instance.AdjustPrice(stock, +2); break;
            case ManipulationType.Plus4: StockMarketManager.Instance.AdjustPrice(stock, +4); break;
            case ManipulationType.Minus1: StockMarketManager.Instance.AdjustPrice(stock, -1); break;
            case ManipulationType.Minus2: StockMarketManager.Instance.AdjustPrice(stock, -2); break;
            case ManipulationType.Minus3: StockMarketManager.Instance.AdjustPrice(stock, -3); break;
            case ManipulationType.Dividend:
                {
                    for (int pid = 0; pid < PlayerManager.Instance.players.Count; pid++)
                    {
                        int cnt = PlayerManager.Instance.players[pid].stocks.TryGetValue(stock, out var c) ? c : 0;
                        if (cnt > 0) PlayerManager.Instance.AddMoney(pid, cnt * 2);
                    }
                    break;
                }
        }
    }

    [Server]
    public int? GetPlayerWithCharacterNumber(int charNum)
    {
        var kv = characterAssignments.FirstOrDefault(x => (int)x.Key.characterNumber == charNum);
        return characterAssignments.ContainsKey(kv.Key) ? kv.Value : (int?)null;
    }

    [Server]
    public bool UndoLast()
    {
        if (ActivePlayerId < 0) return false;
        if (_history.Count == 0) return false;

        // Abilities are barriers: you cannot undo them or go past them
        var top = _history.Peek();
        if (top.type == TurnActionType.Ability && top.undoable == false)
        {
            UIManager.Instance.ShowMessage("Abilities cannot be undone.");
            UIManager.Instance.SetUndoButtonInteractable(false);
            return false;
        }

        // Helpers for anchored prices (with Trader deltas)
        int AnchoredBuy(StockType s)
        {
            int basePrice = _lockedBuyPrice.TryGetValue(s, out var anchor)
                ? anchor
                : StockMarketManager.Instance.stockPrices[s];
            return Mathf.Max(0, basePrice + _buyDeltaThisTurn);
        }

        int AnchoredSell(StockType s)
        {
            int basePrice = _lockedSellPrice.TryGetValue(s, out var anchor)
                ? anchor
                : StockMarketManager.Instance.stockPrices[s];
            return Mathf.Max(0, basePrice + _sellDeltaThisTurn);
        }

        var entry = _history.Pop();

        switch (entry.type)
        {
            case TurnActionType.Buy:
                {
                    int payPrice = AnchoredBuy(entry.stock);
                    PlayerManager.Instance.RemoveStock(ActivePlayerId, entry.stock, 1);
                    PlayerManager.Instance.AddMoney(ActivePlayerId, payPrice);
                    StockMarketManager.Instance.RevertBuy(entry.stock); // price--
                    _buyUsed = Mathf.Max(0, _buyUsed - 1);
                    break;
                }
            case TurnActionType.Sell:
                {
                    if (entry.openSale)
                    {
                        int gain = AnchoredSell(entry.stock);
                        PlayerManager.Instance.AddStock(ActivePlayerId, entry.stock, 1);
                        PlayerManager.Instance.RemoveMoney(ActivePlayerId, gain);
                        StockMarketManager.Instance.RevertOpenSell(entry.stock); // price++
                    }
                    else
                    {
                        PlayerManager.Instance.CancelPendingCloseSell(ActivePlayerId, entry.stock, 1);
                        StockMarketManager.Instance.RemoveQueuedCloseSale(ActivePlayerId, entry.stock, entry.unitPrice);
                    }
                    _sellUsed = Mathf.Max(0, _sellUsed - 1);
                    break;
                }
            case TurnActionType.Ability:
                {
                    // Normally won't reach here (barrier blocks it), but kept for future undoable abilities
                    entry.undo?.Invoke();
                    break;
                }
        }

        if (_buyUsed == 0 && _sellUsed == 0)
        {
            _turnAction = TurnActionType.None;
            _lockedBuyPrice.Clear();
            _lockedSellPrice.Clear();
        }

        // Update Undo button: enabled only if stack not empty and top is not a barrier
        if (_history.Count == 0)
        {
            UIManager.Instance.SetUndoButtonInteractable(false);
        }
        else
        {
            var newTop = _history.Peek();
            bool canUndo = !(newTop.type == TurnActionType.Ability && newTop.undoable == false);
            UIManager.Instance.SetUndoButtonInteractable(canUndo);
        }

        return true;
    }

    [Server]
    public void ScheduleThiefPayout(int thiefPid, int victimPid, int amount)
    {
        _pendingThief.Add((thiefPid, victimPid, Mathf.Max(0, amount)));
    }

    [Server]
    public void UnscheduleThiefPayout(int thiefPid, int victimPid, int amount)
    {
        int idx = _pendingThief.FindIndex(t =>
            t.thiefPid == thiefPid && t.victimPid == victimPid && t.amount == amount);
        if (idx >= 0) _pendingThief.RemoveAt(idx);
    }

    [Server]
    private void ResolveThiefPayoutsFor(int thiefPid)
    {
        for (int i = _pendingThief.Count - 1; i >= 0; i--)
        {
            var t = _pendingThief[i];
            if (t.thiefPid != thiefPid) continue;

            int available = PlayerManager.Instance.players[t.victimPid].money;
            int transfer = Mathf.Min(available, t.amount);
            if (transfer > 0)
            {
                PlayerManager.Instance.RemoveMoney(t.victimPid, transfer);
                PlayerManager.Instance.AddMoney(t.thiefPid, transfer);
            }
            _pendingThief.RemoveAt(i);
        }
    }

    [Server]
    public void ScheduleTaxCollector(int collectorPid, StockType stock)
    => _pendingTaxes.Add((collectorPid, stock));

    [Server]
    public void UnscheduleTaxCollector(int collectorPid, StockType stock)
    {
        int idx = _pendingTaxes.FindIndex(t => t.collectorPid == collectorPid && t.stock == stock);
        if (idx >= 0) _pendingTaxes.RemoveAt(idx);
    }

    [Server]
    public void ResolveTaxesEndOfRound()
    {
        foreach (var t in _pendingTaxes)
        {
            for (int pid = 0; pid < PlayerManager.Instance.players.Count; pid++)
            {
                if (pid == t.collectorPid) continue;

                int cnt = PlayerManager.Instance.players[pid].stocks.TryGetValue(t.stock, out var c) ? c : 0;
                if (cnt <= 0) continue;

                int due = cnt * 1; // $1 per stock card
                int pay = Mathf.Min(due, PlayerManager.Instance.players[pid].money);
                if (pay > 0)
                {
                    PlayerManager.Instance.RemoveMoney(pid, pay);
                    PlayerManager.Instance.AddMoney(t.collectorPid, pay);
                }
            }
        }
        _pendingTaxes.Clear();
    }

    [Server]
    public int? GetPidByCharacterNumber(int num)
    {
        foreach (var kv in characterAssignments)
            if ((int)kv.Key.characterNumber == num)
                return kv.Value;
        return null;
    }

    [Server]
    private int? GetMyCharacterNumber(int pid)
    {
        foreach (var kv in characterAssignments)
            if (kv.Value == pid)
                return (int)kv.Key.characterNumber;
        return null;
    }

    [Server]
    public void BuildTargetsForAbility(int actingPid, CharacterAbilityType ability,
                                   out HashSet<int> enabled, out HashSet<int> disabled)
    {
        // Start with all numbers 1..9
        enabled = new HashSet<int>(Enumerable.Range(1, 9));
        disabled = new HashSet<int>();

        // 1) Remove face-up discards ONLY (these are public knowledge and cannot be chosen)
        var faceUpNums = new HashSet<int>(faceUpDiscards.Select(so => (int)so.characterNumber));
        enabled.ExceptWith(faceUpNums);
        disabled.UnionWith(faceUpNums);

        // 2) Remove self (you can’t target your own character number)
        var myNum = GetMyCharacterNumber(actingPid);
        if (myNum.HasValue)
        {
            enabled.Remove(myNum.Value);
            disabled.Add(myNum.Value);
        }

        // 3) Enforce “can’t be both blocked and stolen” (previous choices this round)
        enabled.ExceptWith(_blockedCharacters);  // already blocked cannot be chosen again
        enabled.ExceptWith(_stolenCharacters);   // already stolen cannot be chosen again
        disabled.UnionWith(_blockedCharacters);
        disabled.UnionWith(_stolenCharacters);

        // 4) Thief-specific rule: cannot target Blocker #1
        if (ability == CharacterAbilityType.Thief)
        {
            enabled.Remove(1);
            disabled.Add(1);
        }

        // NOTE: We do NOT remove numbers that are face-down discards or simply not in play.
        // If a player picks a number with no owner, the ability just fizzles (intended mystery).
    }

    [Server]
    public void RequestCharacterTarget(int actingPid, CharacterAbilityType ability, Action<int> onChosen)
    {
        BuildTargetsForAbility(actingPid, ability, out var enabled, out var disabled);

        if (enabled.Count == 0)
        {
            UIManager.Instance.ShowMessage("No valid targets.");
            return;
        }

        UIManager.Instance.ShowCharacterTargetPanel(actingPid, enabled, disabled, (num) =>
        {
            onChosen?.Invoke(num);
        });
    }

    [Server]
    public bool TryProtectStock(StockType s)
    {
        if (_protectedStocksThisRound.Contains(s)) return false;
        if (!StockMarketManager.Instance.availableStocks.Contains(s)) return false;
        _protectedStocksThisRound.Add(s);
        return true;
    }
    [Server]
    public void UnprotectStock(StockType s) => _protectedStocksThisRound.Remove(s);

    [Server]
    public List<ManipulationType> GetOrCreateManipOptions(int pid)
    {
        if (_cachedManipOptions.TryGetValue(pid, out var list)) return list;

        // Draw & reserve (do NOT discard/return yet)
        var a = DeckManager.Instance.DrawManipulation();
        var b = DeckManager.Instance.DrawManipulation();
        var c = DeckManager.Instance.DrawManipulation();
        list = new List<ManipulationType> { a, b, c };
        _cachedManipOptions[pid] = list;
        return list;
    }

    [Server]
    public void ConsumeManipOptions(int pid, ManipulationType chosen)
    {
        if (!_cachedManipOptions.TryGetValue(pid, out var list) || list.Count != 3) return;

        var rest = new List<ManipulationType>(list);
        rest.Remove(chosen);

        DeckManager.Instance.DiscardManipulation(rest[0]);
        DeckManager.Instance.ReturnManipulationToDeck(rest[1]);

        _cachedManipOptions.Remove(pid);
    }

    [Server]
    public ManipulationType GetOrCreateSingleManip(int pid)
    {
        if (_cachedSingleManip.TryGetValue(pid, out var m)) return m;

        m = DeckManager.Instance.DrawManipulation(); // reserve it
        _cachedSingleManip[pid] = m;
        return m;
    }

    [Server]
    public void ConsumeSingleManip(int pid)
    {
        _cachedSingleManip.Remove(pid);
    }

    [Server]
    public void ResolveEndOfRound()
    {
        // 1) Reveal & apply all queued manipulations (incl. Dividend payouts)
        ResolvePendingManipulationsEndOfRound();

        // After manipulations, some stocks may hit 0 or 8
        // Call your market checks (adjust to your API)
        StockMarketManager.Instance.CheckBankruptcyAndCeilingAll();

        // Remove the sold cards from players' hands
        PlayerManager.Instance.CommitCloseSellsForEndOfRound();

        // 2) Resolve all queued close sells (anchored payouts + market ticks)
        // If your close-sell processing lives in StockMarketManager, call that:
        StockMarketManager.Instance.ProcessCloseSales();

        // Check again after mass selling (could trigger 0/8 again)
        StockMarketManager.Instance.CheckBankruptcyAndCeilingAll();

        // 3) End-of-round taxes (Tax Collector)
        ResolveTaxesEndOfRound();

        // 4) (Optional) Toast / UI note
        UIManager.Instance.ShowMessage("End of round resolved.");
    }

    [Server]
    public void EnableTraderForThisTurn() { _buyDeltaThisTurn = -1; _sellDeltaThisTurn = +1; }
    [Server]
    public void DisableTraderForThisTurn() { _buyDeltaThisTurn = 0; _sellDeltaThisTurn = 0; }
    [Server]
    public bool IsCharacterBlocked(int characterNumber) => _blockedCharacters.Contains(characterNumber);
    [Server]
    public void BlockCharacter(int characterNumber) => _blockedCharacters.Add(characterNumber);
    [Server]
    public void UnblockCharacter(int characterNumber) => _blockedCharacters.Remove(characterNumber);
    [Server]
    public void MarkStolenThisRound(int characterNumber) => _stolenCharacters.Add(characterNumber);

    [Server]
    public void SubmitBid_Server(int pid, int amount)
    {
        // Validate it's currently that pid's bidding turn
        if (pid != _biddingCurrentPid) return;

        var p = PlayerManager.Instance.players.First(pp => pp.id == pid);

        if (amount > 0 && p.money < amount)
        {
            // use CustomNetworkManager’s lookup
            var cnm = NetworkManager.singleton as CustomNetworkManager;
            var np = cnm?.GetPlayerByPid(pid);
            if (np != null)
            {
                np.TargetToast("Not enough money for that bid.");
            }
                
            return; //keep waiting
        }

        _playerBids[pid] = amount;

        if (amount > 0)
        {
            PlayerManager.Instance.RemoveMoney(pid, amount);
            _bidSpendTotal[pid] = (_bidSpendTotal.TryGetValue(pid, out var cur) ? cur : 0) + amount;
        }
        else if (amount < 0)
        {
            PlayerManager.Instance.AddMoney(pid, -amount);
            _bidSpendTotal[pid] = (_bidSpendTotal.TryGetValue(pid, out var cur) ? cur : 0) - amount;
        }

        _biddingWaiting = false;
    }

    [Server]
    public void Server_ConfirmCharacterSelection(int pid, int cardId)
    {
        if (!_selectionWaiting || pid != _selectionCurrentPid)
            return;

        if (_selectionOptions == null || _selectionOptions.Count == 0)
            return;

        var chosen = _selectionOptions.FirstOrDefault(c => (int)c.characterNumber == cardId);

        if (chosen == null)
        {
            Debug.LogWarning($"[Server_ConfirmCharacterSelection] No character with id={cardId} in current options.");
            return;
        }

        Debug.Log($"[SELECT] Confirmed pid={pid} -> #{(int)chosen.characterNumber}-{chosen.characterName}");

        characterAssignments[chosen] = pid;
        _selectionOptions.Remove(chosen);
        PlayerManager.Instance.players[pid].selectedCard = chosen;

        RpcHideCharacterSelection();

        _selectionWaiting = false;
    }


    [ClientRpc]
    private void RpcShowCharacterSelection(int pickerPid, int[] optionIds)
    {
        if (UIManager.Instance == null) return;

        if (UIManager.Instance.LocalPlayerId <0)
        {
            UIManager.Instance.CachePendingSelection(pickerPid, optionIds);
            Debug.Log($"[SELECTION] Cached pending selection: picker={pickerPid}");
            return;
        }

        bool isLocal = (UIManager.Instance.LocalPlayerId == pickerPid);
        UIManager.Instance.ShowCharacterSelection(pickerPid, optionIds, isLocal);
    }

    [ClientRpc]
    private void RpcHideCharacterSelection()
    {
        if (UIManager.Instance == null)
            return;

        UIManager.Instance.HideCharacterSelection();
    }

    [Server]
    public void CleanupRound()
    {
        // Reset round state for next round
        discardDeck = null;
        faceUpDiscards?.Clear(); // ?. null conditional operator
        faceDownDiscards?.Clear();
        availableCharacters?.Clear();
        biddingOrder?.Clear();
        selectionOrder?.Clear();
        characterAssignments?.Clear();
    }

    [Server]
    private void Shuffle<T>(List<T> list) // Fisher–Yates shuffle
    {
        for (int i = 0; i < list.Count; i++)
        {
            int r = UnityEngine.Random.Range(i, list.Count);
            var tmp = list[i]; 
            list[i] = list[r]; 
            list[r] = tmp;
        }
    }
}