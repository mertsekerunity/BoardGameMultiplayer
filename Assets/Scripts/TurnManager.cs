using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Mirror;
using UnityEngine;
using static Unity.Collections.AllocatorManager;

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

    private readonly int[] _bidOptionAmounts = new int[]
    {
        -1, // slot 0
        -1, // slot 1
         0, // slot 2
         0, // slot 3
         1, // slot 4
         3, // slot 5
         5, // slot 6
         7, // slot 7
        10  // slot 8
    };

    private readonly Dictionary<int, int> _bidTakenBySlot = new Dictionary<int, int>(); // (slotIndex -> pid)

    private float _biddingPanelCloseTimer = 2.5f;
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
    private class PendingStockTarget
    {
        public int pickerPid;
        public HashSet<StockType> candidates;
        public Action<StockType> onChosen;
        public Action onCancelled;
    }
    private class PendingCharacterTarget
    {
        public int pickerPid;
        public CharacterAbilityType ability;
        public HashSet<int> candidates;
        public Action<int> onChosen;
        public Action onCancelled;
    }

    private PendingCharacterTarget _pendingCharacterTarget;

    private PendingStockTarget _pendingStockTarget;

    public event Action BiddingFinished;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        //DontDestroyOnLoad(transform.root.gameObject); //didnt work with mirror, still dont know why.
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

        int[] faceUpIds = faceUpDiscards.Select(c => (int)c.characterNumber).ToArray();

        RpcShowFaceUpDiscards(faceUpIds);
    }

    [Server]
    public void StartBiddingPhase()
    {
        Debug.Log("Bidding Phase started.");

        biddingOrder = PlayerManager.Instance.players.OrderBy(p => p.money).Select(p => p.id).ToList();

        _playerBids.Clear();
        _bidTakenBySlot.Clear();

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

        // wait before closing panel
        yield return new WaitForSeconds(_biddingPanelCloseTimer);

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

        ResolveThiefPayoutsForVictim(pid);

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

        var nm = NetworkManager.singleton as CustomNetworkManager;
        var np = nm?.GetPlayerByPid(ActivePlayerId);
        np?.TargetHidePrivateManipPeek();

        var card = PlayerManager.Instance.players[ActivePlayerId].selectedCard;

        int cardId = (int)card.characterNumber;
        RpcHideActiveCharacter(ActivePlayerId, cardId);

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
        {
            foreach (var m in kv.Value)
            {
                DeckManager.Instance.ReturnManipulationToDeck(m);
            }
                
        }

        _cachedManipOptions.Clear();

        foreach (var kv in _cachedSingleManip)
        {
            DeckManager.Instance.ReturnManipulationToDeck(kv.Value);
        }
            
        _cachedSingleManip.Clear();
    }

    [Server]
    private void PushAbilityBarrier(Action undo = null)
    {
        _abilityAvailable = false;

        var nm = NetworkManager.singleton as CustomNetworkManager;
        var np = nm?.GetPlayerByPid(ActivePlayerId);

        np?.TargetSetAbilityButtonState(false);
    }

    [Server]
    public void RaiseLimitsForThisTurn(int buyLimit, int sellLimit)
    {
        _buyLimitThisTurn = buyLimit;
        _sellLimitThisTurn = sellLimit;
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
        if (ActivePlayerId < 0) return false;

        var nm = NetworkManager.singleton as CustomNetworkManager;
        var np = nm?.GetPlayerByPid(ActivePlayerId);

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

        Server_SyncPlayerState(ActivePlayerId);
        Server_SyncStockPrice(stock);

        np?.TargetSetUndoInteractable(true);

        return true;
    }

    [Server]
    public bool TrySellOne(StockType stock, bool openSale)
    {
        if (ActivePlayerId < 0) return false;

        var nm = NetworkManager.singleton as CustomNetworkManager;
        var np = nm?.GetPlayerByPid(ActivePlayerId);

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

            Server_SyncPlayerState(ActivePlayerId);
            Server_SyncStockPrice(stock);
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

            Server_SyncPlayerState(ActivePlayerId);
            if (!ok) return false;

            // Queue market payout at the anchored price; price tick happens at end-of-round
            StockMarketManager.Instance.QueueCloseSale(ActivePlayerId, stock, anchoredGain, current);
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

        np?.TargetSetUndoInteractable(true);

        return true;
    }

    [Server]
    public bool TryUseAbility()
    {
        if (ActivePlayerId < 0 || !_abilityAvailable) return false;

        var nm = NetworkManager.singleton as CustomNetworkManager;
        var np = nm?.GetPlayerByPid(ActivePlayerId);

        var myNum = GetMyCharacterNumber(ActivePlayerId);
        if (myNum.HasValue && IsCharacterBlocked(myNum.Value))
        {
            np.TargetToastAbilityBlocked();
           
            _abilityAvailable = false;
            np?.TargetSetAbilityButtonState(false);
            return false;
        }

        if (_activeAbility == CharacterAbilityType.Blocker || _activeAbility == CharacterAbilityType.Thief)
        {
            return Server_TryUseBlockerOrThief();
        }

        if (_activeAbility == CharacterAbilityType.Trader)
        {
            return Server_TryUseTrader();
        }

        if (_activeAbility == CharacterAbilityType.Manipulator)
        {
            return Server_TryUseManipulator();
        }

        if (_activeAbility == CharacterAbilityType.LotteryWinner)
        {
            return Server_TryUseLotteryWinner();
        }

        if (_activeAbility == CharacterAbilityType.Broker)
        {
            return Server_TryUseBroker();
        }

        if (_activeAbility == CharacterAbilityType.Gambler)
        {
            return Server_TryUseGambler();
        }

        if (_activeAbility == CharacterAbilityType.TaxCollector)
        {
            return Server_TryUseTaxCollector();
        }

        if (_activeAbility == CharacterAbilityType.Inheritor)
        {
            return Server_TryUseInheritor();
        }

        np?.TargetToast("Ability not implemented yet.");
        return false;
    }

    [Server]
    private bool Server_TryUseBlocker(int actingPid, int targetCharacterNumber)
    {
        if (IsCharacterBlocked(targetCharacterNumber))
            return false;

        var ownerPidOpt = GetPidByCharacterNumber(targetCharacterNumber);
        if (ownerPidOpt == null)
            return false;

        if (ownerPidOpt.Value == actingPid)
            return false;

        BlockCharacter(targetCharacterNumber);

        Debug.Log($"[BLOCKER] #{targetCharacterNumber} is now blocked.");
        return true;
    }

    [Server]
    private bool Server_TryUseThief(int actingPid, int targetCharacterNumber)
    {
        if (targetCharacterNumber == 1) //cant target blocker #1
            return false;

        if (IsCharacterBlocked(targetCharacterNumber)) //cant steal from blocked target
            return false;

        if (GetPidByCharacterNumber(targetCharacterNumber) is not int victimPid)
            return false;

        if (victimPid == actingPid)
            return false;

        MarkStolenThisRound(targetCharacterNumber);

        int victimMoney = PlayerManager.Instance.players[victimPid].money;
        int stealAmount = (int)Mathf.Floor((float)victimMoney / 2f);

        if (stealAmount <= 0)
            return false;

        ScheduleThiefPayout(thiefPid: actingPid, victimPid: victimPid, amount: stealAmount);

        Debug.Log($"[THIEF] P{actingPid} will steal {stealAmount}$ from P{victimPid} at end of round.");
        return true;
    }

    [Server]
    private bool Server_TryUseTrader()
    {
        var nm = NetworkManager.singleton as CustomNetworkManager;
        var np = nm?.GetPlayerByPid(ActivePlayerId);
        if (np == null)
            return false;

        EnableTraderForThisTurn();

        PushAbilityBarrier();
        return true;
    }

    [Server]
    private bool Server_TryUseInheritor()
    {
        var nm = NetworkManager.singleton as CustomNetworkManager;
        var np = nm?.GetPlayerByPid(ActivePlayerId);
        if (np == null)
            return false;

        var stock = DeckManager.Instance.DrawRandomStock();
        PlayerManager.Instance.AddStock(ActivePlayerId, stock, 1);

        Server_SyncPlayerState(ActivePlayerId);

        np.TargetToast($"You inherited 1 {stock}.");

        // Ability consumed, no undo.
        PushAbilityBarrier();
        return true;
    }

    [Server]
    private bool Server_TryUseManipulator()
    {
        var nm = NetworkManager.singleton as CustomNetworkManager;
        var np = nm?.GetPlayerByPid(ActivePlayerId);
        if (np == null)
            return false;

        var trio = GetOrCreateManipOptions(ActivePlayerId);
        if (trio == null || trio.Count == 0)
        {
            np.TargetToast("No manipulation options available.");
            return false;
        }

        int[] ids = trio.Select(m => (int)m).ToArray();
        string prompt = $"{PlayerManager.Instance.players[ActivePlayerId].playerName}, choose a manipulation";

        np.TargetAskManipChoice(prompt, ids);
        return true;
    }

    [Server]
    public void Server_OnManipOptionChosen(int pickerPid, int manipId)
    {
        if (pickerPid != ActivePlayerId)
            return;

        var nm = NetworkManager.singleton as CustomNetworkManager;
        var np = nm?.GetPlayerByPid(pickerPid);
        if (np == null)
            return;

        var trio = GetOrCreateManipOptions(pickerPid);
        var chosen = (ManipulationType)manipId;
        if (!trio.Contains(chosen))
            return;

        var enabledStocks = new HashSet<StockType>(
            StockMarketManager.Instance.availableStocks
                .Where(s => !_manipulatedStocksThisRound.Contains(s) &&
                            !_protectedStocksThisRound.Contains(s)));

        if (enabledStocks.Count == 0)
        {
            np.TargetToast("No stocks available to manipulate.");
            return;
        }

        string manipPrompt = $"{PlayerManager.Instance.players[pickerPid].playerName}, choose a stock to apply manipulation";

        RequestManipStockTarget(
            pickerPid,
            enabledStocks,
            manipPrompt,
            onChosen: (applyStock) =>
            {
                if (!TryReserveSpecificManipulationTarget(applyStock))
                {
                    np.TargetToast("That stock can't be manipulated (already reserved).");
                    return;
                }

                QueueManipulation(pickerPid, chosen, applyStock);
                ConsumeManipOptions(pickerPid, chosen);
                Server_NotifyManipQueued(pickerPid, chosen, applyStock);

                var protectCandidates = new HashSet<StockType>(
                    StockMarketManager.Instance.availableStocks
                        .Where(s => s != applyStock &&
                                    !_protectedStocksThisRound.Contains(s))
                );

                if (protectCandidates.Count == 0)
                {
                    PushAbilityBarrier();
                    return;
                }

                string protectPrompt = $"{PlayerManager.Instance.players[pickerPid].playerName}, choose a stock to protect";

                RequestManipStockTarget(
                    pickerPid,
                    protectCandidates,
                    protectPrompt,
                    onChosen: (protectStock) =>
                    {
                        if (!TryProtectStock(protectStock))
                        {
                            np.TargetToast("That stock is already protected.");
                            PushAbilityBarrier();
                            return;
                        }

                        Server_NotifyProtectionQueued(pickerPid, protectStock);
                        PushAbilityBarrier();
                    },
                    onCancelled: () =>
                    {
                        PushAbilityBarrier();
                    });
            },
            onCancelled: () =>
            {
                // ability still can be used.
            });
    }

    [Server]
    public void Server_OnManipOptionCancelled(int pickerPid)
    {
        if (pickerPid != ActivePlayerId)
            return;

    }

    [Server]
    private void RequestManipStockTarget(int pickerPid, HashSet<StockType> candidates, string prompt, Action<StockType> onChosen, Action onCancelled)
    {
        var nm = NetworkManager.singleton as CustomNetworkManager;
        var np = nm?.GetPlayerByPid(pickerPid);
        if (np == null)
        {
            onCancelled?.Invoke();
            return;
        }

        _pendingStockTarget = new PendingStockTarget
        {
            pickerPid = pickerPid,
            candidates = new HashSet<StockType>(candidates),
            onChosen = onChosen,
            onCancelled = onCancelled
        };

        int[] ids = candidates.Select(s => (int)s).ToArray();
        np.TargetAskManipStockTarget(prompt, ids);
    }

    [Server]
    private bool Server_TryUseTaxCollector()
    {
        var enabled = new HashSet<StockType>(StockMarketManager.Instance.availableStocks);
        if (enabled.Count == 0)
        {
            var nm = NetworkManager.singleton as CustomNetworkManager;
            var np = nm?.GetPlayerByPid(ActivePlayerId);
            np?.TargetToast("No stocks available for taxes.");
            return false;
        }

        string pName = PlayerManager.Instance.players[ActivePlayerId].playerName;
        string prompt = $"{pName}, choose stock to apply taxes";

        RequestStockTarget(
            pickerPid: ActivePlayerId,
            candidates: enabled,
            prompt: prompt,
            confirmPrefix: "Apply taxes to",
            onChosen: stock =>
            {
                ScheduleTaxCollector(ActivePlayerId, stock);

                // Ability consumed
                PushAbilityBarrier(undo: null);
            },
            onCancelled: () =>
            {
                // do nothing
            });

        return true;
    }

    [Server]
    private bool Server_TryUseBlockerOrThief()
    {
        var nm = NetworkManager.singleton as CustomNetworkManager;
        var np = nm?.GetPlayerByPid(ActivePlayerId);
        if (np == null)
            return false;

        RequestCharacterTarget(
        actingPid: ActivePlayerId,
        ability: _activeAbility,
        onChosen: chosenNum =>
        {
            bool ok = false;

            if (_activeAbility == CharacterAbilityType.Blocker)
            {
                ok = Server_TryUseBlocker(ActivePlayerId, chosenNum);
            }
            else if (_activeAbility == CharacterAbilityType.Thief)
            {
                ok = Server_TryUseThief(ActivePlayerId, chosenNum);
            }

            if (!ok)
            {
                np.TargetToast("Ability had no effect.");
                return;
            }

            PushAbilityBarrier();
        },
        onCancelled: () =>
        {
            // cancelled, ability still can be used.
        });

        return true;
    }

    [Server]
    private bool Server_TryUseGambler()
    {
        var nm = NetworkManager.singleton as CustomNetworkManager;
        var np = nm?.GetPlayerByPid(ActivePlayerId);
        if (np == null)
            return false;

        int money = PlayerManager.Instance.players[ActivePlayerId].money;

        if (money < 3)
        {
            np.TargetToast("Not enough money to gamble.");
            return false;
        }

        np.TargetAskGamble(money);
        return true;
    }

    [Server]
    public void Server_OnGambleChosen(int pickerPid, int cardsToTake)
    {
        if (pickerPid != ActivePlayerId) return;
        if (_activeAbility != CharacterAbilityType.Gambler) return;
        if (cardsToTake <= 0) return;

        int money = PlayerManager.Instance.players[ActivePlayerId].money;

        if (cardsToTake == 2 && money < 6)
        {
            if (money >= 3)
                cardsToTake = 1;
            else
                return;
        }
        else if (cardsToTake == 1 && money < 3)
        {
            return;
        }

        var taken = new List<StockType>();

        for (int i = 0; i < cardsToTake; i++)
        {
            var s = DeckManager.Instance.DrawRandomStock();
            PlayerManager.Instance.AddStock(ActivePlayerId, s, 1);
            taken.Add(s);
        }

        PlayerManager.Instance.RemoveMoney(ActivePlayerId, 3 * cardsToTake);

        Server_SyncPlayerState(ActivePlayerId);

        PushAbilityBarrier(undo: null);
    }

    [Server]
    public void Server_OnGambleCancelled(int pickerPid)
    {
        if (pickerPid != ActivePlayerId) return;
        if (_activeAbility != CharacterAbilityType.Gambler) return;
    }

    [Server]
    private bool Server_TryUseLotteryWinner()
    {
        var nm = NetworkManager.singleton as CustomNetworkManager;
        var np = nm?.GetPlayerByPid(ActivePlayerId);
        if (np == null)
            return false;

        int payout = DeckManager.Instance.ClaimLottery();
        PlayerManager.Instance.AddMoney(ActivePlayerId, payout);

        Server_SyncPlayerState(ActivePlayerId);
        GameManager.Instance.Server_SyncRoundAndLottery();

        var m = GetOrCreateSingleManip(ActivePlayerId);

        np.TargetShowPrivateManipPeek(m);

        var enabled = new HashSet<StockType>(
            StockMarketManager.Instance.availableStocks.Where(s => !_manipulatedStocksThisRound.Contains(s) && !_protectedStocksThisRound.Contains(s)));

        if (enabled.Count == 0)
        {
            DeckManager.Instance.ReturnManipulationToDeck(m);
            ConsumeSingleManip(ActivePlayerId);

            np.TargetToast("No stocks available to manipulate.");

            PushAbilityBarrier();
            return false;
        }

        string playerName = PlayerManager.Instance.players[ActivePlayerId].playerName;
        string prompt = $"{playerName}, choose a stock to apply manipulation";

        RequestStockTarget(
            pickerPid: ActivePlayerId,
            candidates: enabled,
            prompt: prompt,
            confirmPrefix: "Apply manipulation to",
            onChosen: stock =>
            {
                if (!TryReserveSpecificManipulationTarget(stock))
                {
                    np.TargetToast("That stock can't be manipulated.");

                    DeckManager.Instance.ReturnManipulationToDeck(m);
                    ConsumeSingleManip(ActivePlayerId);

                    return;
                }

                QueueManipulation(ActivePlayerId, m, stock);
                Server_NotifyManipQueued(ActivePlayerId, m, stock);
                ConsumeSingleManip(ActivePlayerId);

                PushAbilityBarrier();
            },
            onCancelled: () =>
            {
                np.TargetHidePrivateManipPeek();
            });

        return true;
    }

    [Server]
    private bool Server_TryUseBroker()
    {
        var nm = NetworkManager.singleton as CustomNetworkManager;
        var np = nm?.GetPlayerByPid(ActivePlayerId);
        if (np == null)
            return false;

        RaiseLimitsForThisTurn(buyLimit: 3, sellLimit: 4);

        var m = GetOrCreateSingleManip(ActivePlayerId);

        np.TargetShowPrivateManipPeek(m);

        var enabled = new HashSet<StockType>(
            StockMarketManager.Instance.availableStocks
                .Where(s => !_manipulatedStocksThisRound.Contains(s) &&
                            !_protectedStocksThisRound.Contains(s))
        );

        string playerName = PlayerManager.Instance.players[ActivePlayerId].playerName;

        if (enabled.Count == 0)
        {
            DeckManager.Instance.ReturnManipulationToDeck(m);
            ConsumeSingleManip(ActivePlayerId);

            np.TargetToast("No stocks available to manipulate. Limits increased for this turn.");

            PushAbilityBarrier();
            return true;
        }

        string prompt = $"{playerName}, choose a stock to apply manipulation";

        RequestStockTarget(
            pickerPid: ActivePlayerId,
            candidates: enabled,
            prompt: prompt,
            confirmPrefix: "Apply manipulation to",
            onChosen: stock =>
            {

                if (!TryReserveSpecificManipulationTarget(stock))
                {
                    np.TargetToast("That stock can't be manipulated.");

                    DeckManager.Instance.ReturnManipulationToDeck(m);
                    ConsumeSingleManip(ActivePlayerId);
                    return;
                }

                QueueManipulation(ActivePlayerId, m, stock);
                Server_NotifyManipQueued(ActivePlayerId,m,stock);
                ConsumeSingleManip(ActivePlayerId);

                PushAbilityBarrier();
            },
            onCancelled: () =>
            {
                np.TargetHidePrivateManipPeek();       
            });

        return true;
    }

    [ClientRpc]
    private void RpcSyncPlayerState(int pid, int money, int[] stockTypeIds, int[] stockCounts, int[] pendingStockTypeIds, int[] pendingCounts)
    {
        var stocks = new Dictionary<StockType, int>();

        for (int i = 0; i < stockTypeIds.Length && i < stockCounts.Length; i++)
        {
            stocks[(StockType)stockTypeIds[i]] = stockCounts[i];
        }

        var pending = new Dictionary<StockType, int>();
        for (int i = 0; i < pendingStockTypeIds.Length && i < pendingCounts.Length; i++)
        {
            pending[(StockType)pendingStockTypeIds[i]] = pendingCounts[i];
        }

        var display = new Dictionary<StockType, int>();

        foreach (var stock in StockMarketManager.Instance.availableStocks)
        {
            int ownedCount = stocks.TryGetValue(stock, out var c) ? c : 0;
            int pendingCount = pending.TryGetValue(stock, out var p) ? p : 0;

            display[stock] = Mathf.Max(0, ownedCount - pendingCount);
        }

        UIManager.Instance.SyncPlayerState(pid, money, display, pending);
    }

    [Server]
    public void Server_SyncPlayerState(int pid)
    {
        var pl = PlayerManager.Instance.players[pid];

        var owned = pl.stocks.Where(kv => kv.Value > 0).ToList();
        int[] stockIds = owned.Select(kv => (int)kv.Key).ToArray();
        int[] stockCounts = owned.Select(kv => kv.Value).ToArray();

        var pendingDict = PlayerManager.Instance.GetPendingCloseDict(pid);
        var pendingList = pendingDict?.Where(kv => kv.Value > 0).ToList();
        int[] pendingIds = pendingList.Select(kv => (int)kv.Key).ToArray();
        int[] pendingCounts = pendingList.Select(kv => kv.Value).ToArray();

        RpcSyncPlayerState(pid, pl.money, stockIds, stockCounts, pendingIds, pendingCounts);
    }

    [Server]
    public void Server_SyncStockPrice(StockType stock)
    {
        int price = StockMarketManager.Instance.stockPrices[stock];
        RpcSyncStockPrice(stock, price);
    }

    [ClientRpc]
    private void RpcSyncStockPrice(StockType stock, int newPrice)
    {
        StockMarketManager.Instance.stockPrices[stock] = newPrice;

        StockMarketManager.Instance.RaiseStockPriceChanged(stock, newPrice);
    }

    [Server]
    private void RequestStockTarget(int pickerPid, HashSet<StockType> candidates, string prompt, string confirmPrefix, Action<StockType> onChosen, Action onCancelled)
    {
        var nm = NetworkManager.singleton as CustomNetworkManager;
        var np = nm?.GetPlayerByPid(pickerPid);
        if (np == null)
        {
            Debug.LogError($"[TAX] No NetPlayer for pid={pickerPid}");
            onCancelled?.Invoke();
            return;
        }

        _pendingStockTarget = new PendingStockTarget
        {
            pickerPid = pickerPid,
            candidates = new HashSet<StockType>(candidates),
            onChosen = onChosen,
            onCancelled = onCancelled
        };

        int[] ids = candidates.Select(s => (int)s).ToArray();
        np.TargetAskStockTarget(prompt, confirmPrefix, ids);
    }

    [Server]
    public void Server_OnStockTargetChosen(int pickerPid, int stockId)
    {
        if (_pendingStockTarget == null)
            return;

        if (_pendingStockTarget.pickerPid != pickerPid)
            return;

        var stock = (StockType)stockId;
        if (!_pendingStockTarget.candidates.Contains(stock))
            return;

        var cb = _pendingStockTarget.onChosen;
        _pendingStockTarget = null;

        cb?.Invoke(stock);
    }

    [Server]
    public void Server_OnStockTargetCancelled(int pickerPid)
    {
        if (_pendingStockTarget == null)
            return;

        if (_pendingStockTarget.pickerPid != pickerPid)
            return;

        var cancel = _pendingStockTarget.onCancelled;
        _pendingStockTarget = null;

        cancel?.Invoke();
    }

    [Server]
    public void Server_OnCharacterTargetChosen(int pickerPid, int charNum)
    {
        if (_pendingCharacterTarget == null)
            return;

        if (_pendingCharacterTarget.pickerPid != pickerPid)
            return;

        if (!_pendingCharacterTarget.candidates.Contains(charNum))
            return;

        var cb = _pendingCharacterTarget.onChosen;
        _pendingCharacterTarget = null;

        cb?.Invoke(charNum);
    }

    [Server]
    public void Server_OnCharacterTargetCancelled(int pickerPid)
    {
        if (_pendingCharacterTarget == null)
            return;

        if (_pendingCharacterTarget.pickerPid != pickerPid)
            return;

        var cancel = _pendingCharacterTarget.onCancelled;
        _pendingCharacterTarget = null;

        cancel?.Invoke();
    }

    [Server]
    private void Server_NotifyManipQueued(int pid, ManipulationType m, StockType s)
    {
        var nm = NetworkManager.singleton as CustomNetworkManager;
        var np = nm?.GetPlayerByPid(pid);
        if (np == null)
            return;

        np.TargetOnManipQueued(m, s);
    }

    [Server]
    private void Server_NotifyProtectionQueued(int pid, StockType s)
    {
        var nm = NetworkManager.singleton as CustomNetworkManager;
        var np = nm?.GetPlayerByPid(pid);
        if (np == null)
            return;

        np.TargetOnProtectionQueued(s);
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
    public void QueueManipulation(int playerId, ManipulationType card, StockType stock)
    {
        var pm = new PendingManipulation { playerId = playerId, card = card, stock = stock };
        _pendingManipulations.Add(pm);
    }

    [Server]
    public void ResolvePendingManipulationsEndOfRound()
    {
        if (_pendingManipulations.Count > 0)
        {
            int n = _pendingManipulations.Count;
            var manipIds = new int[n];
            var stockIds = new int[n];

            for (int i = 0; i < n; i++)
            {
                manipIds[i] = (int)_pendingManipulations[i].card;
                stockIds[i] = (int)_pendingManipulations[i].stock;
            }

            RpcRevealRoundManipTags(manipIds, stockIds);
        }
            
        foreach (var pm in _pendingManipulations)
        {
            ApplyManipulationNow(pm.card, pm.stock);
        }

        _pendingManipulations.Clear();
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

        var nm = NetworkManager.singleton as CustomNetworkManager;
        var np = nm?.GetPlayerByPid(ActivePlayerId);
        
        if (_history.Count == 0)
        {
            np?.TargetToast("Nothing to undo.");
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

                    Server_SyncPlayerState(ActivePlayerId);
                    Server_SyncStockPrice(entry.stock);
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

                        Server_SyncPlayerState(ActivePlayerId);
                        Server_SyncStockPrice(entry.stock);
                    }
                    else
                    {
                        PlayerManager.Instance.CancelPendingCloseSell(ActivePlayerId, entry.stock, 1);
                        StockMarketManager.Instance.RemoveQueuedCloseSale(ActivePlayerId, entry.stock, entry.unitPrice);

                        Server_SyncPlayerState(ActivePlayerId);
                    }
                    _sellUsed = Mathf.Max(0, _sellUsed - 1);
                    break;
                }
        }

        if (_buyUsed == 0 && _sellUsed == 0)
        {
            _turnAction = TurnActionType.None;
            _lockedBuyPrice.Clear();
            _lockedSellPrice.Clear();
        }

        bool canUndo = (_history.Count > 0);
        np?.TargetSetUndoInteractable(canUndo);

        return true;
    }

    [Server]
    public void ScheduleThiefPayout(int thiefPid, int victimPid, int amount)
    {
        _pendingThief.Add((thiefPid, victimPid, Mathf.Max(0, amount)));
    }

    [Server]
    private void ResolveThiefPayoutsForVictim(int victimPid)
    {
        if (_pendingThief == null || _pendingThief.Count == 0)
            return;

        var nm = NetworkManager.singleton as CustomNetworkManager;

        for (int i = _pendingThief.Count - 1; i >= 0; i--)
        {
            var t = _pendingThief[i];
            if (t.victimPid != victimPid) continue;

            int available = PlayerManager.Instance.players[t.victimPid].money;
            int transfer = Mathf.Min(available, t.amount);
            if (transfer > 0)
            {
                PlayerManager.Instance.RemoveMoney(t.victimPid, transfer);
                PlayerManager.Instance.AddMoney(t.thiefPid, transfer);

                var thiefNp = nm?.GetPlayerByPid(t.thiefPid);
                var victimNp = nm?.GetPlayerByPid(t.victimPid);

                string thiefName = PlayerManager.Instance.players[t.thiefPid].playerName;
                string victimName = PlayerManager.Instance.players[t.victimPid].playerName;

                thiefNp?.TargetToast($"You stole {transfer}$ from {victimName}.");
                victimNp?.TargetToast($"{thiefName} stole {transfer}$ from you.");

                Server_SyncPlayerState(t.thiefPid);
                Server_SyncPlayerState(t.victimPid);
            }
            _pendingThief.RemoveAt(i);
        }
    }

    [Server]
    public void ScheduleTaxCollector(int collectorPid, StockType stock) => _pendingTaxes.Add((collectorPid, stock));

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
    public void RequestCharacterTarget(int actingPid, CharacterAbilityType ability, Action<int> onChosen, Action onCancelled)
    {
        BuildTargetsForAbility(actingPid, ability, out var enabled, out var disabled);

        var nm = NetworkManager.singleton as CustomNetworkManager;
        var np = nm?.GetPlayerByPid(actingPid);

        if (np == null)
        {
            Debug.LogError($"[TARGET] No NetPlayer for pid={actingPid}");
            onCancelled?.Invoke();
            return;
        }

        if (enabled.Count == 0)
        {
            np.TargetToast("No valid targets.");
            onCancelled?.Invoke();
            return;
        }

        _pendingCharacterTarget = new PendingCharacterTarget
        {
            pickerPid = actingPid,
            ability = ability,
            candidates = new HashSet<int>(enabled),
            onChosen = onChosen,
            onCancelled = onCancelled
        };

        int[] enabledArr = enabled.ToArray();
        int[] disabledArr = disabled.ToArray();

        string prompt = $"{PlayerManager.Instance.players[actingPid].playerName}, " + $"choose a character to use {ability} on";

        np.TargetAskCharacterTarget(prompt, enabledArr, disabledArr, (int)ability);
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

        Server_SyncAllPlayers();
        Server_SyncAllStocks();

        var nm = NetworkManager.singleton as CustomNetworkManager; //delete later
        foreach (var p in PlayerManager.Instance.players)
        {
            var np = nm?.GetPlayerByPid(p.id);
            np?.TargetToast("End of round resolved.");
        }
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
    public void SubmitBid_Server(int pid, int slotIndex)
    {
        var nm = NetworkManager.singleton as CustomNetworkManager;
        var np = nm?.GetPlayerByPid(pid);

        if (!_biddingWaiting || pid != _biddingCurrentPid)
        {
            np?.TargetToast("Not your bidding turn.");
            return;
        }

        if (slotIndex < 0 || slotIndex >= _bidOptionAmounts.Length)
        {
            np?.TargetToast("Invalid bid option.");
            return;
        }

        if (_bidTakenBySlot.TryGetValue(slotIndex, out var existingPid) && existingPid != pid)
        {
            np?.TargetToast("That option is already taken.");
            return;
        }

        var p = PlayerManager.Instance.players.First(pp => pp.id == pid);

        if (p == null)
        {
            np?.TargetToast("Unknown player.");
            return;
        }

        int amount = _bidOptionAmounts[slotIndex];

        if (amount > 0 && p.money < amount)
        {
            np.TargetToast("Not enough money for that bid.");
                
            return; //keep waiting
        }

        _bidTakenBySlot[slotIndex] = pid;
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

        Server_SyncPlayerState(pid);

        RpcOnBidChosen(pid, slotIndex, amount);

        _biddingWaiting = false;
    }

    [ClientRpc]
    private void RpcOnBidChosen(int pid, int slotIndex, int amount)
    {
        UIManager.Instance.Bidding_MarkChoice(pid, slotIndex, amount);
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

    [ClientRpc]
    private void RpcRevealRoundManipTags(int[] manipIds, int[] stockIds)
    {
        if (manipIds == null || stockIds == null) return;

        var list = new List<(ManipulationType, StockType)>();

        for (int i = 0; i < manipIds.Length && i < stockIds.Length; i++)
        {
            var m = (ManipulationType)manipIds[i];
            var s = (StockType)stockIds[i];
            list.Add((m, s));
        }

        UIManager.Instance.RevealRoundManipTagsForAll(list);
    }

    [Server]
    public void Server_SyncAllPlayers()
    {
        var pm = PlayerManager.Instance;
        foreach (var pl in pm.players)
        {
            Server_SyncPlayerState(pl.id);
        }
    }

    [Server]
    public void Server_SyncAllStocks()
    {
        var sm = StockMarketManager.Instance;
        foreach (var s in sm.availableStocks)
        {
            Server_SyncStockPrice(s);
        }
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