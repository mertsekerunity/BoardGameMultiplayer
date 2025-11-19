using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using static Unity.Collections.AllocatorManager;

public class UIManager : MonoBehaviour
{
    public static UIManager Instance { get; private set; }

    [SerializeField] private bool debugShowManipToAll = false;

    [SerializeField] private TextMeshProUGUI globalPrompt;

    [SerializeField] private Transform playersPanelContainer;
    [SerializeField] private PlayerPanel playerPanelPrefab;

    [SerializeField] private Transform marketPanelContainer;
    [SerializeField] private MarketRow marketRowPrefab;

    [SerializeField] private TextMeshProUGUI lotteryText;

    [SerializeField] private Image characterImage;

    [SerializeField] private CharacterTargetPanel characterTargetPanel;

    [SerializeField] private ConfirmationPanel confirmationPanel;
    [SerializeField] private TextMeshProUGUI confirmText;
    [SerializeField] private Button yesButton;
    [SerializeField] private Button noButton;

    [SerializeField] private ManipulationChoicePanel manipulationChoicePanel;
    [SerializeField] private StockTargetPanel stockTargetPanel;

    [SerializeField] private TextMeshProUGUI privateManipPeek;

    [Header("Face-Up Discards")]
    [SerializeField] private TextMeshProUGUI discard1;
    [SerializeField] private TextMeshProUGUI discard2;

    [Header("Bidding")]
    [SerializeField] private BiddingPanel biddingPanel;
    [SerializeField] private TextMeshProUGUI biddingOrderPrompt; // “Player X, make a bid”

    [Header("Market Icons")]
    [SerializeField] private Sprite redStockIcon;
    [SerializeField] private Sprite blueStockIcon;
    [SerializeField] private Sprite greenStockIcon;
    [SerializeField] private Sprite yellowStockIcon;

    [Header("Character Selection")]
    //[SerializeField] private GameObject selectionRoot;        // panel root
    [SerializeField] private Transform characterSelectionPanel;   // grid/vertical group
    [SerializeField] private CharacterSelectionItem selectionItemPrefab;
    [SerializeField] private TextMeshProUGUI selectionPrompt; // “Player X, choose your character”

    // A map from playerId → instantiated panel
    private Dictionary<int, PlayerPanel> _playerPanels = new Dictionary<int, PlayerPanel>();
    private Dictionary<StockType, MarketRow> _marketRows = new Dictionary<StockType, MarketRow>();

    private int _localPlayerId = -1;
    private int _activePlayerId = -1;

    public int LocalPlayerId => _localPlayerId;  // read-only accessor
    public bool HasLocalPlayer => _localPlayerId >= 0;

    private int[] _cachedIds;
    private string[] _cachedNames;
    private int[] _cachedMoney;

    private bool _gameUiInitialized;

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;
        //DontDestroyOnLoad(transform.root.gameObject);
    }

    void Start()
    {
        SetUndoButtonVisible(false);
        SetUndoButtonInteractable(false);

        // Subscribe to events
        PlayerManager.Instance.OnPendingCloseChanged += HandlePendingCloseChanged;
        PlayerManager.Instance.OnMoneyChanged += HandleMoneyChanged;
        PlayerManager.Instance.OnStocksChanged += HandleStocksChanged;
        StockMarketManager.Instance.OnStockPriceChanged += HandlePriceChanged;
        StockMarketManager.Instance.OnStockBankrupt += ShowBankruptcyUI;
        StockMarketManager.Instance.OnStockCeilingHit += ShowCeilingUI;
        DeckManager.Instance.OnManipulationCardDrawn += ShowManipulationCard;
        DeckManager.Instance.OnTaxCardDrawn += ShowTaxCard;
        DeckManager.Instance.OnDecksReshuffled += RefreshDeckUI;
        DeckManager.Instance.OnLotteryChanged += HandleLotteryChanged;
        TurnManager.Instance.OnManipulationQueuedUI += HandleManipQueued;
        TurnManager.Instance.OnManipulationRemovedUI += HandleManipRemoved;
        TurnManager.Instance.OnProtectionChosenUI += HandleProtectionChosen;
    }

    void OnDestroy()
    {
        if (PlayerManager.Instance != null)
        {
            PlayerManager.Instance.OnPendingCloseChanged -= HandlePendingCloseChanged;
            PlayerManager.Instance.OnMoneyChanged -= HandleMoneyChanged;
            PlayerManager.Instance.OnStocksChanged -= HandleStocksChanged;
        }
            
        if (StockMarketManager.Instance != null)
        {
            StockMarketManager.Instance.OnStockPriceChanged -= HandlePriceChanged;
            StockMarketManager.Instance.OnStockBankrupt -= ShowBankruptcyUI;
            StockMarketManager.Instance.OnStockCeilingHit -= ShowCeilingUI;
        }
        if (DeckManager.Instance != null)
        {
            DeckManager.Instance.OnManipulationCardDrawn -= ShowManipulationCard;
            DeckManager.Instance.OnTaxCardDrawn -= ShowTaxCard;
            DeckManager.Instance.OnDecksReshuffled -= RefreshDeckUI;
            DeckManager.Instance.OnLotteryChanged -= HandleLotteryChanged;
        }
        if (TurnManager.Instance != null)
        {
            TurnManager.Instance.OnManipulationQueuedUI -= HandleManipQueued;
            TurnManager.Instance.OnManipulationRemovedUI -= HandleManipRemoved;
            TurnManager.Instance.OnProtectionChosenUI -= HandleProtectionChosen;
        }
    }

    private static string TagFor(ManipulationType m) => m switch
    {
        ManipulationType.Plus1 => "+1",
        ManipulationType.Plus2 => "+2",
        ManipulationType.Plus4 => "+4",
        ManipulationType.Minus1 => "-1",
        ManipulationType.Minus2 => "-2",
        ManipulationType.Minus3 => "-3",
        ManipulationType.Dividend => "Dividend",
        _ => "?"
    };

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.Space))
        {
            TurnManager.Instance.EndActivePlayerTurn(); // advance
        }
            
        if (Input.GetKeyDown(KeyCode.Tab))   // take control of whoever is active
        {
            var active = TurnManager.Instance.ActivePlayerId;
            if (active >= 0 && active != _localPlayerId)
            {
                _localPlayerId = active;
                SetActivePlayer(active, true);
                ShowMessage($"Now controlling Player {active + 1}");
            }
        }
    }

    public void CreatePlayerPanels(int[] ids, string[] names, int[] money)
    {
        int playerCount = ids.Length;
        Debug.Log($"[UI] CreatePlayerPanels: players={playerCount}, local={_localPlayerId}"); // REMOVE LATER

        foreach (Transform child in playersPanelContainer)
        {
            Destroy(child.gameObject);
        }
        _playerPanels.Clear();

        if (playerCount < 5)
        {
            playersPanelContainer.GetComponent<GridLayoutGroup>().constraintCount = 2;
            playersPanelContainer.GetComponent<GridLayoutGroup>().constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        }
        else
        {
            playersPanelContainer.GetComponent<GridLayoutGroup>().constraintCount = 3;
            playersPanelContainer.GetComponent<GridLayoutGroup>().constraint = GridLayoutGroup.Constraint.FixedRowCount;
        }

        for (int i = 0; i < playerCount; i++)
        {
            int pid = ids[i];
            string nm = names[i];
            int cash = money[i];

            bool isLocal = HasLocalPlayer && (pid == _localPlayerId);

            var panel = Instantiate(playerPanelPrefab, playersPanelContainer);

            panel.Initialize(pid, nm, isLocal);
            panel.UpdateMoney(cash);

            if (isLocal)
            {
                //panel.UpdateStocks(pData.stocks);
                // Stocks: we’ll hook proper syncing later
                panel.UpdateStocks(new Dictionary<StockType, int>());
            }
                
            _playerPanels[pid] = panel;
        }

        //// Make sure interactivity respects local id
        //if (_localPlayerId >= 0)
        //{
        //    SetLocalPlayerId(_localPlayerId);
        //}
    }

    public void CreateMarketRows()
    {
        // Clear existing
        foreach (Transform child in marketPanelContainer)
        {
            Destroy(child.gameObject);
        }
        _marketRows.Clear();

        foreach (var stock in StockMarketManager.Instance.availableStocks)
        {
            var row = Instantiate(marketRowPrefab, marketPanelContainer);
            // Determine icon based on stock type
            Sprite icon = stock switch
            {
                StockType.Red => redStockIcon,
                StockType.Blue => blueStockIcon,
                StockType.Green => greenStockIcon,
                StockType.Yellow => yellowStockIcon,
                _ => null
            };
            // Initialize buy-only with icon
            row.Initialize(stock, icon, OnBuyStock);

            int starting = StockMarketManager.Instance.stockPrices[stock];
            row.UpdatePrice(starting);

            _marketRows[stock] = row;
        }
    }

    private void HandleStocksChanged(int playerId, Dictionary<StockType, int> newStocks)
    {
        if (playerId != _localPlayerId) return;  // only show stocks for local player
        if (_playerPanels.TryGetValue(playerId, out var panel))
            panel.UpdateStocks(newStocks);
    }

    private void HandlePriceChanged(StockType stock, int newPrice)
    {
        Debug.Log($"[UI] PriceChanged {stock} -> {newPrice}"); // remove later

        if (_marketRows.TryGetValue(stock, out var row))
        {
            row.UpdatePrice(newPrice);
        }
    }

    private void HandlePendingCloseChanged(int playerId, StockType changedStock, int pending)
    {
        // Only the local player sees their pending reductions
        if (playerId != _localPlayerId)
        {
            return;
        }

        // Build a display map = owned - pending for ALL stocks
        var p = PlayerManager.Instance.players.First(pp => pp.id == playerId);
        var display = new Dictionary<StockType, int>();

        foreach (var stock in StockMarketManager.Instance.availableStocks)
        {
            int owned = p.stocks.TryGetValue(stock, out var c) ? c : 0;
            int pend = PlayerManager.Instance.GetPendingCloseCount(playerId, stock);
            display[stock] = Mathf.Max(0, owned - pend);
        }

        // Push to the local panel
        if (_playerPanels.TryGetValue(playerId, out var panel))
        {
            panel.UpdateStocks(display);
            panel.UpdatePendingClose(changedStock, pending);
        }
    }

    public void OnBuyStock(StockType stock)
    {
        var lp = LocalNetPlayer;
        if (lp == null) 
        {   
            ShowMessage("No local network player.");
            return; 
        }

        if (_activePlayerId < 0)
        {
            ShowMessage("Round hasn't started yet.");
            return;
        }

        if (_localPlayerId != _activePlayerId)
        {
            ShowMessage("Not your turn.");
            return;
        }

        lp.CmdBuy(stock);
    }

    public void OnSellStock(StockType stock, bool openSale)
    {
        var lp = LocalNetPlayer;
        if (lp == null)
        {
            ShowMessage("No local network player.");
            return;
        }

        if (_activePlayerId < 0)
        {
            ShowMessage("Round hasn't started yet.");
            return;
        }

        if (_localPlayerId != _activePlayerId)
        {
            ShowMessage("Not your turn.");
            return;
        }

        lp.CmdSell(stock, openSale);
    }

    public void OnUseAbilityClicked()
    {
        var lp = LocalNetPlayer;
        if (lp == null)
        {
            ShowMessage("No local network player.");
            return;
        }

        if (_activePlayerId < 0)
        {
            ShowMessage("Round hasn't started yet.");
            return;
        }

        if (_localPlayerId != _activePlayerId)
        {
            ShowMessage("Not your turn.");
            return;
        }

        lp.CmdUseAbility();
    }

    public void SetAbilityButtonState(bool enabled)
    {
        if (_playerPanels.TryGetValue(_localPlayerId, out var panel))
        {
            panel.SetAbilityButtonInteractable(enabled && (_activePlayerId == _localPlayerId));
        }   
    }

    public void SetActivePlayer(int playerId, bool enable = true)
    {
        _activePlayerId = enable ? playerId : -1;

        bool isLocalTurn = enable && (playerId == _localPlayerId);

        SetUndoButtonVisible(isLocalTurn);
        //SetUndoButtonInteractable(isLocalTurn && TurnManager.Instance.CanUndoCurrentTurn);
        SetUndoButtonInteractable(isLocalTurn); // always clickable now

        foreach (var row in _marketRows.Values)
        {
            row.SetBuyInteractable(isLocalTurn);
        }

        foreach (var kv in _playerPanels)
        {
            kv.Value.SetActiveHighlight(kv.Key == playerId);
            int pid = kv.Key;
            var panel = kv.Value;

            bool isLocalPanel = (pid == _localPlayerId);
            if (isLocalPanel)
            {
                panel.SetSellButtonsInteractable(isLocalTurn);
                panel.SetAbilityButtonInteractable(isLocalTurn);
                panel.SetEndTurnButtonInteractable(isLocalTurn);
            }
            else
            {
                panel.SetSellButtonsInteractable(false);
                panel.SetAbilityButtonInteractable(false);
                panel.SetEndTurnButtonInteractable(false);
            }
        }
    }

    public void HandleLotteryChanged(int amount)
    {
        lotteryText.text = $"{amount}$";
    }

    public void OnUndoButton()
    {
        var lp = LocalNetPlayer;
        if (lp == null)
        {
            ShowMessage("No local network player.");
            return;
        }

        if (_activePlayerId < 0)
        {
            ShowMessage("Round hasn't started yet.");
            return;
        }

        if (_localPlayerId != _activePlayerId)
        {
            ShowMessage("Not your turn.");
            return;
        }

        lp.CmdUndo();
    }

    public void OnEndTurn()
    {
        var lp = LocalNetPlayer;
        if (lp == null)
        {
            ShowMessage("No local network player.");
            return;
        }

        if (_activePlayerId < 0)
        {
            ShowMessage("Round hasn't started yet.");
            return;
        }

        if (_localPlayerId != _activePlayerId)
        {
            ShowMessage("Not your turn.");
            return;
        }

        LocalNetPlayer.CmdEndTurn();
    }

    // Show character card and player name when turn starts
    public void ShowCharacter(CharacterCardSO card, int playerId)
    {
        characterImage.sprite = card.characterSprite;
        characterImage.gameObject.SetActive(true);
    }

    public void HideCharacter(CharacterCardSO card, int playerId)
    {
        characterImage.sprite = card.characterSprite;
        characterImage.gameObject.SetActive(false);
    }

    // Display generic messages
    public void ShowMessage(string message)
    {
        // TODO: Implement popup or log
        Debug.Log("[MSG] " + message);
    }

    // Called whenever a player’s money changes
    private void HandleMoneyChanged(int playerId, int newAmount)
    {
        if (_playerPanels.TryGetValue(playerId, out var panel))
        {
            panel.UpdateMoney(newAmount);
            Debug.Log(newAmount); //remove later
        }
    }

    public void ShowBiddingPanel(bool show)
    {
        if (!biddingPanel) return;
        biddingPanel.gameObject.SetActive(show);
    }

    public void Bidding_Reset(int playerCount)
    {
        if (!biddingPanel) return;
        biddingPanel.ResetForNewBidding(playerCount);
    }

    public void Bidding_BeginTurn(string playerName, int playerMoney)
    {
        if (!biddingPanel) return;

        biddingPanel.BeginTurn(playerName, playerMoney, (amount) =>
        {
            var lp = LocalNetPlayer;
            if (lp == null)
            {
                ShowMessage("No local network player.");
                return;
            }
            lp.CmdSubmitBid(amount);
        });
    }

    public void Bidding_Close()
    {
        if (!biddingPanel) return;
        biddingPanel.Close();
        biddingOrderPrompt.gameObject.SetActive(false);
    }

    public void SetBidActivePlayer(int playerId)
    {
        foreach (var kv in _playerPanels)
            kv.Value.SetActiveHighlight(kv.Key == playerId);
            var pName = PlayerManager.Instance.players.First(p => p.id == playerId).playerName;
            if (biddingOrderPrompt != null)
                {
                biddingOrderPrompt.gameObject.SetActive(true);
                biddingOrderPrompt.text = $"{pName}, make a bid";
                }
    }

    public void ClearAllHighlights()
    {
        foreach (var kv in _playerPanels)
            kv.Value.SetActiveHighlight(false);
    }

    public void ShowCharacterTargetPanel(
    int actingPid,
    HashSet<int> enabled,
    HashSet<int> disabled,
    Action<int> onPick)
    {
        if (!characterTargetPanel) return;

        bool isLocal = (actingPid == _localPlayerId);

        var cg = characterTargetPanel.GetComponent<CanvasGroup>();
        if (cg)
        {
            cg.alpha = isLocal ? 1f : 0f;           // hide from non-local clients
            cg.interactable = isLocal;              // block clicks
            cg.blocksRaycasts = isLocal;            // block pointer hits
        }

        if (isLocal)
            characterTargetPanel.Show(enabled, disabled, onPick);
        else
            characterTargetPanel.Hide();            // ensure it’s not visible here
    }

    public void HideCharacterTargetPanel()
    {
        if (characterTargetPanel == null) return;
        characterTargetPanel.Hide();
    }

    public void ShowFaceUpDiscards(List<CharacterCardSO> cards)
    {
        HideFaceUpDiscards(); // clear first

        if (cards.Count > 0)
        {
            discard1.gameObject.SetActive(true);
            discard1.text = $"{cards[0].characterNumber}-{cards[0].characterName}";
        }

        if (cards.Count > 1)
        {
            discard2.gameObject.SetActive(true);
            discard2.text = $"{cards[1].characterNumber}-{cards[1].characterName}";
        }
    }

    public void HideFaceUpDiscards()
    {
        discard1.gameObject.SetActive(false);
        discard2.gameObject.SetActive(false);
    }

    public void ShowSelectionConfirm(int actingPid, string message, Action onYes, Action onNo)
    {
        // During selection you usually want *only the picker* to confirm.
        bool allow = (actingPid == _localPlayerId);
        //bool allow = (actingPid == _localPlayerId);
        ShowConfirm(allow, message, onYes, onNo, tag: "SELECT");
    }

    public void ShowAbilityConfirm(int actingPid, string message, Action onYes, Action onNo)
    {
        // During abilities, only the acting player should confirm.
        bool allow = (actingPid == _localPlayerId);
        //bool allow = (actingPid == _localPlayerId);
        ShowConfirm(allow, message, onYes, onNo, tag: "ABILITY");
    }

    private void ShowConfirm(bool allow, string message, Action onYes, Action onNo, string tag)
    {
        if (!confirmationPanel) {return; }

        confirmationPanel.gameObject.SetActive(true);
        //confirmationPanel.transform.SetAsLastSibling(); // bring on top

        if (confirmText) confirmText.text = message;

        var cg = confirmationPanel.GetComponent<CanvasGroup>();
        if (cg) { cg.interactable = true; cg.blocksRaycasts = true; }

        yesButton.onClick.RemoveAllListeners();
        noButton.onClick.RemoveAllListeners();

        yesButton.interactable = allow;

        yesButton.onClick.AddListener(() =>
        {
            confirmationPanel.gameObject.SetActive(false);
            if (allow) onYes?.Invoke();
        });

        noButton.onClick.AddListener(() =>
        {
            confirmationPanel.gameObject.SetActive(false);
            onNo?.Invoke();
        });
    }

    public void ShowManipulationChoice(
        int actingPid,
        List<ManipulationType> cards,
        string promptText,
        Action<ManipulationType, ManipulationType, ManipulationType, ManipulationType> onChosenOrCancelled)
    {
        bool isLocal = (actingPid == _localPlayerId);
        if (!isLocal) return; // only the acting local player sees this

        ShowPrompt(promptText); // use global prompt (single label in your layout)

        manipulationChoicePanel.Show(actingPid, cards, onChosenOrCancelled);
    }

    public void ShowStockTargetPanel(
        int actingPid,
        HashSet<StockType> enabled,
        string promptText,
        Action<StockType> onChosen,
        Action onCancelled)
    {
        bool isLocal = (actingPid == _localPlayerId);
        if (!isLocal) return;

        ShowPrompt(promptText);
        stockTargetPanel.Show(
            actingPid,
            enabled,
            "", // hide panel-local prompt; we’re using the global one
            onChosen: s =>
            {
                HidePrompt();
                onChosen?.Invoke(s);
            },
            onCancelled: () =>
            {
                HidePrompt();
                onCancelled?.Invoke();
            });
    }

    public void SetUndoButtonInteractable(bool interactable)
    {
        if (_playerPanels.TryGetValue(_localPlayerId, out var localPanel))
            localPanel.SetUndoInteractable(interactable);
    }

    public void SetUndoButtonVisible(bool visible)
    {
        if (_playerPanels.TryGetValue(_localPlayerId, out var localPanel))
            localPanel.SetUndoVisible(visible);
    }

    public void Selection_Show(int pickerPid, List<CharacterCardSO> options, Action<CharacterCardSO> onChooseConfirmed)
    {
        characterSelectionPanel.gameObject.SetActive(true);
        foreach (Transform c in characterSelectionPanel) Destroy(c.gameObject);

        var pName = PlayerManager.Instance.players.First(p => p.id == pickerPid).playerName;
        if (selectionPrompt != null)
        {
            selectionPrompt.gameObject.SetActive(true);
            selectionPrompt.text = $"{pName}, choose your character";
        }

        // Only show clickable items to the picker (or everyone if debug)
        bool allow = (pickerPid == _localPlayerId);
        //bool allow = (pickerPid == _localPlayerId);

        foreach (var card in options)
        {
            var item = Instantiate(selectionItemPrefab, characterSelectionPanel);
            var captured = card;
            item.Bind(captured, () =>
            {
                if (!allow) return;
                ShowSelectionConfirm(
                    pickerPid,
                    $"Choose #{(int)captured.characterNumber} - {captured.characterName}?",
                    onYes: () => onChooseConfirmed?.Invoke(captured),
                    onNo: () => { }
                );
            });
            item.SetInteractable(allow);
        }
    }

    public void Selection_Hide()
    {
        selectionPrompt.gameObject.SetActive(false);
        characterSelectionPanel.gameObject.SetActive(false);
        foreach (Transform c in characterSelectionPanel) Destroy(c.gameObject);
    }

    public void HideAllUndoButtons()
    {
        foreach (var kv in _playerPanels)
        {
            kv.Value.SetUndoVisible(false);
            kv.Value.SetUndoInteractable(false);
        }
    }

    private void HandleManipQueued(int pid, ManipulationType m, StockType s)
    {
        if (!debugShowManipToAll && pid != _localPlayerId) return;
        if (_marketRows.TryGetValue(s, out var row))
            row.SetPrivateTag(TagFor(m));
    }

    private void HandleManipRemoved(int pid, ManipulationType m, StockType s)
    {
        if (!debugShowManipToAll && pid != _localPlayerId) return;
        if (_marketRows.TryGetValue(s, out var row))
            row.ClearPrivateTag();
    }

    private void HandleProtectionChosen(int pid, StockType s)
    {
        if (!debugShowManipToAll && pid != _localPlayerId) return;
        if (_marketRows.TryGetValue(s, out var row))
            row.SetPrivateTag("Protected");
    }

    public void ClearAllMarketSecretTags()
    {
        foreach (var row in _marketRows.Values)
            row.ClearPrivateTag();
    }

    public void ShowPrivateManipPeek(int actingPid, ManipulationType m)
    {
        if (!privateManipPeek) return;

        bool isLocal = debugShowManipToAll || (actingPid == _localPlayerId);
        privateManipPeek.gameObject.SetActive(isLocal);
        if (isLocal)
            privateManipPeek.text = $"drawn manipulation: {TagFor(m)}";
    }

    public void HidePrivateManipPeek()
    {
        if (privateManipPeek)
            privateManipPeek.gameObject.SetActive(false);
    }

    public void ShowPrompt(string text)
    {
        if (!globalPrompt) return;
        globalPrompt.gameObject.SetActive (true);
        globalPrompt.text = text;
        //globalPrompt.text = text ?? "";
        //globalPrompt.gameObject.SetActive(!string.IsNullOrEmpty(text));
    }

    public void HidePrompt()
    {
        if (!globalPrompt) return;
        globalPrompt.gameObject.SetActive(false);
    }

    public void OnRoundStartUIReset()
    {
        foreach (var kv in _playerPanels) kv.Value.ClearPendingCloseAll();
    }


    private void ShowBankruptcyUI(StockType stock)
    {
        string text = $"{stock} stock went bankrupt";
        ShowPrompt(text);
    }

    private void ShowCeilingUI(StockType stock)
    {
        string text = $"{stock} stock hit ceiling";
        ShowPrompt(text);
    }

    public void SetLocalPlayerId(int pid)
    {
        Debug.Log($"[UI] SetLocalPlayerId -> {pid}"); // REMOVE LATER
        _localPlayerId = pid;

        if (!_gameUiInitialized && _cachedIds != null)
        {
            BuildGameUI();
        }

        if (_playerPanels == null || _playerPanels.Count == 0)
        {
            return;
        }

        // retag panels so only your seat is interactive on your turn
        foreach (var kv in _playerPanels)
        {
            int panelPid = kv.Key;
            var panel = kv.Value;
            bool isLocal = (kv.Key == _localPlayerId);

            panel.SetSellButtonsInteractable(isLocal);
            panel.SetAbilityButtonInteractable(isLocal);
            panel.SetEndTurnButtonInteractable(isLocal);
        }

        // if a turn is already active, recompute interactivity
        if (_activePlayerId >= 0)
        {
            SetActivePlayer(_activePlayerId, enable: true);
        }

        // refresh local panel snapshot 
        //we need new rpc to refresh local panel snapshot later !!!

        ShowMessage($"You are Player {pid + 1}.");
    }

    public void InitializeGameUI(int[] ids, string[] names, int[] money)
    {
        int playerCount = ids.Length;
        Debug.Log($"[UI] InitializeGameUI players={playerCount}, local={_localPlayerId}");

        _cachedIds = (int[])ids.Clone();
        _cachedNames = (string[])names.Clone();
        _cachedMoney = (int[])money.Clone();

        if (_localPlayerId < 0)
        {
            Debug.Log("[UI] there is no local player id yet.");
            return;
        }

        BuildGameUI();
    }

    private void BuildGameUI()
    {
        if (_cachedIds == null) return;

        Debug.Log($"[UI] BuildGameUI using local={_localPlayerId}");

        CreatePlayerPanels(_cachedIds, _cachedNames, _cachedMoney);
        CreateMarketRows();
        HideAllUndoButtons();
        // gerekiyorsa diğer UI setup fonksiyonların...
        // CreateQuestsUI();
        // CreateCharacterCardsUI();
        // vs.

        _gameUiInitialized = true;
    }


    public void InitializeLocalPlayerUI(int pid)
    {
        Debug.Log($"[UI] InitializeLocalPlayerUI pid={pid}");

        SetLocalPlayerId(pid);
    }


    private NetPlayer LocalNetPlayer =>
        Mirror.NetworkClient.isConnected ? Mirror.NetworkClient.localPlayer?.GetComponent<NetPlayer>() : null;

    private void ShowManipulationCard(ManipulationType card)
    {
        // TODO: Display the drawn manipulation card
    }

    private void ShowTaxCard(TaxType card)
    {
        // TODO: Display the drawn tax card
    }

    private void RefreshDeckUI()
    {
        // TODO: Refresh deck counts or images
    }
}
