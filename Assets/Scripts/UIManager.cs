using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class UIManager : MonoBehaviour
{
    public static UIManager Instance { get; private set; }

    [SerializeField] private Transform playersPanelContainer;
    [SerializeField] private PlayerPanel playerPanelPrefab;

    [SerializeField] private Transform marketPanelContainer;
    [SerializeField] private MarketRow marketRowPrefab;

    [SerializeField] private TextMeshProUGUI roundText;
    [SerializeField] private TextMeshProUGUI lotteryText;
    [SerializeField] private TextMeshProUGUI winnerText;

    [SerializeField] private Image characterImage;

    [SerializeField] private CharacterTargetPanel characterTargetPanel;

    [SerializeField] private ConfirmationPanel confirmationPanel;
    [SerializeField] private TextMeshProUGUI confirmText;
    [SerializeField] private Button yesButton;
    [SerializeField] private Button noButton;

    [SerializeField] private ManipulationChoicePanel manipulationChoicePanel;
    [SerializeField] private StockTargetPanel stockTargetPanel;

    [SerializeField] private TextMeshProUGUI privateManipPeek;

    [Header("Prompts")]
    [SerializeField] private TextMeshProUGUI globalPrompt;
    [SerializeField] private TextMeshProUGUI localPrompt;

    [Header("Face-Up Discards")]
    [SerializeField] private TextMeshProUGUI discard1;
    [SerializeField] private TextMeshProUGUI discard2;

    [Header("Bidding")]
    [SerializeField] private BiddingPanel biddingPanel;

    [Header("Market Icons")]
    [SerializeField] private Sprite redStockIcon;
    [SerializeField] private Sprite blueStockIcon;
    [SerializeField] private Sprite greenStockIcon;
    [SerializeField] private Sprite yellowStockIcon;

    [Header("Character Selection")]
    [SerializeField] private Transform characterSelectionPanel;
    [SerializeField] private CharacterSelectionItem selectionItemPrefab;

    [Header("Player Aid Panel")]
    [SerializeField] private PlayerAidPanel playerAidPanel;
    [SerializeField] private Button playerAidButton;

    private Dictionary<int, string> _playerNames = new Dictionary<int, string>();

    private class PendingPlayerState
    {
        public int money;
        public Dictionary<StockType, int> stocks;
    }

    private Dictionary<int, PendingPlayerState> _pendingPlayerStates = new Dictionary<int, PendingPlayerState>();

    private struct PendingSelection
    {
        public int pickerPid;
        public int[] optionIds;
    }

    private bool _hasPendingSelection;
    private PendingSelection _pendingSelection;

    private bool _biddingActive;
    public bool CanTogglePlayerAid => !_biddingActive;

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
        //DontDestroyOnLoad(transform.root.gameObject); //didnt work with mirror, still dont know why.
    }

    void Start()
    {
        SetUndoButtonVisible(false);
        SetUndoButtonInteractable(false);

        // Subscribe to events
        StockMarketManager.Instance.OnStockPriceChanged += HandlePriceChanged;
    }

    void OnDestroy()
    {
        if (StockMarketManager.Instance != null)
        {
            StockMarketManager.Instance.OnStockPriceChanged -= HandlePriceChanged;
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

            _playerPanels[pid] = panel;
            _playerNames[pid] = nm;

            if (_pendingPlayerStates.TryGetValue(pid, out var pending))
            {
                panel.UpdateMoney(pending.money);
                panel.UpdateStocks(pending.stocks);
                _pendingPlayerStates.Remove(pid);
            }
            else
            {
                panel.UpdateMoney(cash);

                if (isLocal)
                {
                    panel.UpdateStocks(new Dictionary<StockType, int>());
                }
            }
        }
    }

    public void CreateMarketRows()
    {
        foreach (Transform child in marketPanelContainer)
        {
            Destroy(child.gameObject);
        }
        _marketRows.Clear();

        foreach (var stock in StockMarketManager.Instance.availableStocks)
        {
            var row = Instantiate(marketRowPrefab, marketPanelContainer);

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

    private void HandlePriceChanged(StockType stock, int newPrice)
    {
        if (_marketRows.TryGetValue(stock, out var row))
        {
            row.UpdatePrice(newPrice);
        }
    }

    public void OnBuyStock(StockType stock)
    {
        var lp = LocalNetPlayer;
        if (lp == null) 
        {
            ShowLocalToast("No local network player.");
            return; 
        }

        if (_activePlayerId < 0)
        {
            ShowLocalToast("Round hasn't started yet.");
            return;
        }

        if (_localPlayerId != _activePlayerId)
        {
            ShowLocalToast("Not your turn.");
            return;
        }

        lp.CmdBuy(stock);
    }

    public void OnSellStock(StockType stock, bool openSale)
    {
        var lp = LocalNetPlayer;
        if (lp == null)
        {
            ShowLocalToast("No local network player.");
            return;
        }

        if (_activePlayerId < 0)
        {
            ShowLocalToast("Round hasn't started yet.");
            return;
        }

        if (_localPlayerId != _activePlayerId)
        {
            ShowLocalToast("Not your turn.");
            return;
        }

        lp.CmdSell(stock, openSale);
    }

    public void OnUseAbilityClicked()
    {
        var lp = LocalNetPlayer;
        if (lp == null)
        {
            ShowLocalToast("No local network player.");
            return;
        }

        if (_activePlayerId < 0)
        {
            ShowLocalToast("Round hasn't started yet.");
            return;
        }

        if (_localPlayerId != _activePlayerId)
        {
            ShowLocalToast("Not your turn.");
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
        SetUndoButtonInteractable(isLocalTurn);

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

    public void SetLotteryAmount(int amount)
    {
        lotteryText.text = $"{amount}$";
    }

    public void SetRoundNumber(int round)
    {
        roundText.text = round.ToString();
    }

    public void OnUndoButton()
    {
        var lp = LocalNetPlayer;
        if (lp == null)
        {
            ShowLocalToast("No local network player.");
            return;
        }

        if (_activePlayerId < 0)
        {
            ShowLocalToast("Round hasn't started yet.");
            return;
        }

        if (_localPlayerId != _activePlayerId)
        {
            ShowLocalToast("Not your turn.");
            return;
        }

        lp.CmdUndo();
    }

    public void OnEndTurn()
    {
        var lp = LocalNetPlayer;
        if (lp == null)
        {
            ShowLocalToast("No local network player.");
            return;
        }

        if (_activePlayerId < 0)
        {
            ShowLocalToast("Round hasn't started yet.");
            return;
        }

        if (_localPlayerId != _activePlayerId)
        {
            ShowLocalToast("Not your turn.");
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

    public void ShowBiddingPanel(bool show)
    {
        if (!biddingPanel) return;
        biddingPanel.gameObject.SetActive(show);
    }

    public void Bidding_Reset(int playerCount)
    {
        if (!biddingPanel) return;
        biddingPanel.ResetForNewBidding(playerCount);

        _biddingActive = true;

        playerAidPanel.ForceHide();
        playerAidButton.interactable = false;
    }

    public void Bidding_BeginTurn(string playerName, int playerMoney)
    {
        if (!biddingPanel) return;

        biddingPanel.BeginTurn(playerName, playerMoney, (slotIndex) =>
        {
            var lp = LocalNetPlayer;
            if (lp == null)
            {
                ShowLocalToast("No local network player.");
                return;
            }
            lp.CmdSubmitBid(slotIndex);
        });
    }

    public void Bidding_MarkChoice(int pid, int slotIndex, int amount)
    {
        if (!biddingPanel) return;

        string name;

        if (_playerNames != null && _playerNames.TryGetValue(pid, out var storedName) && !string.IsNullOrEmpty(storedName))
        {
            name = storedName;
        }

        else
        {
            name = $"Player {pid + 1}";
        }

        biddingPanel.MarkChoice(pid, slotIndex, name);
    }

    public void Bidding_Close()
    {
        if (!biddingPanel) return;
        biddingPanel.Close();

        _biddingActive = false;

        if (playerAidButton != null)
        {
            playerAidButton.interactable = true;
        }
    }

    public void SetBidActivePlayer(int playerId)
    {
        string pName;

        foreach (var kv in _playerPanels)
        {
            kv.Value.SetActiveHighlight(kv.Key == playerId);
        }

        if (!_playerNames.TryGetValue(playerId, out pName) || string.IsNullOrEmpty(pName))
        {
            pName = $"Player {playerId + 1}";
        }

        ShowLocalToast($"{pName}, make a bid");
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
    Action<int> onChosen)
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
            characterTargetPanel.Show(enabled, disabled, onChosen);
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

        ShowLocalToast(promptText);

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

        ShowLocalToast(promptText);

        stockTargetPanel.Show(
            actingPid,
            enabled,
            "", // hide panel-local prompt; we’re using the global one
            onChosen: s =>
            {
                onChosen?.Invoke(s);
            },
            onCancelled: () =>
            {
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

    public void ShowCharacterSelection(int pickerPid, int[] optionIds, bool isLocal)
    {
        string pName = GetPlayerNameById(pickerPid);

        if (!isLocal)
        {
            HideCharacterSelection();
            return;
        }

        ShowLocalToast($"{pName}, choose your character");

        characterSelectionPanel.gameObject.SetActive(true);
        foreach (Transform c in characterSelectionPanel) Destroy(c.gameObject);


        var allChars = TurnManager.Instance.characterDeck;
        var options = allChars.Where(c => optionIds.Contains((int)c.characterNumber)).ToList();

        bool allow = isLocal;

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
                    onYes: () =>
                    {
                        if (LocalNetPlayer == null)
                        {
                            return;
                        }

                        int cardId = (int)captured.characterNumber;
                        LocalNetPlayer.CmdConfirmCharacterSelection(cardId);
                    },
                    onNo: () => { }
                );
            });

            item.SetInteractable(allow);
        }
    }

    public void HideCharacterSelection()
    {
        characterSelectionPanel.gameObject.SetActive(false);
        foreach (Transform c in characterSelectionPanel) Destroy(c.gameObject);
    }

    public void CachePendingSelection(int pickerPid, int[] optionIds)
    {
        _pendingSelection = new PendingSelection
        {
            pickerPid = pickerPid,
            optionIds = optionIds
        };
        _hasPendingSelection = true;
    }

    private string GetPlayerNameById(int pid)
    {
        int idx = Array.IndexOf(_cachedIds,pid);
        return _cachedNames[idx];
    }


    public void HideAllUndoButtons()
    {
        foreach (var kv in _playerPanels)
        {
            kv.Value.SetUndoVisible(false);
            kv.Value.SetUndoInteractable(false);
        }
    }

    public void HandleManipQueued(int pid, ManipulationType m, StockType s)
    {
        if (pid != _localPlayerId) return;
        if (_marketRows.TryGetValue(s, out var row))
            row.SetPrivateTag(TagFor(m));
    }

    public void HandleProtectionChosen(int pid, StockType s)
    {
        if (pid != _localPlayerId) return;
        if (_marketRows.TryGetValue(s, out var row))
            row.SetPrivateTag("Protected");
    }

    public void RevealRoundManipTagsForAll(List<(ManipulationType m, StockType s)> list)
    {
        if (list == null) return;

        foreach (var (m, s) in list)
        {
            if (_marketRows.TryGetValue(s, out var row))
            {
                row.SetPrivateTag(TagFor(m));
            }
        }
    }

    public void ClearAllMarketSecretTags()
    {
        foreach (var row in _marketRows.Values)
            row.ClearPrivateTag();
    }

    public void ShowPrivateManipPeek(int actingPid, ManipulationType m)
    {
        if (!privateManipPeek) return;

        bool isLocal = (actingPid == _localPlayerId);
        privateManipPeek.gameObject.SetActive(isLocal);
        if (isLocal)
            privateManipPeek.text = $"drawn manipulation: {TagFor(m)}";
    }

    public void HidePrivateManipPeek()
    {
        if (privateManipPeek)
            privateManipPeek.gameObject.SetActive(false);
    }

    public void OnRoundStartUIReset()
    {
        foreach (var kv in _playerPanels) kv.Value.ClearPendingCloseAll();
    }

    public void ShowBankruptcyUI(StockType stock)
    {
        string text = $"{stock} stock went bankrupt";
        ShowGlobalBanner(text);
    }

    public void ShowCeilingUI(StockType stock)
    {
        string text = $"{stock} stock hit ceiling";
        ShowGlobalBanner(text);
    }

    public void SetLocalPlayerId(int pid)
    {
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

        if (_hasPendingSelection)
        {
            var ps = _pendingSelection;
            _hasPendingSelection = false;

            bool isLocal = (pid == ps.pickerPid);

            ShowCharacterSelection(ps.pickerPid, ps.optionIds, isLocal);
        }
    }

    public void InitializeGameUI(int[] ids, string[] names, int[] money)
    {
        int playerCount = ids.Length;

        _cachedIds = (int[])ids.Clone();
        _cachedNames = (string[])names.Clone();
        _cachedMoney = (int[])money.Clone();

        if (_localPlayerId < 0)
        {
            return;
        }

        BuildGameUI();
    }

    private void BuildGameUI()
    {
        if (_cachedIds == null) return;

        CreatePlayerPanels(_cachedIds, _cachedNames, _cachedMoney);
        CreateMarketRows();
        HideAllUndoButtons();

        _gameUiInitialized = true;
    }

    public void InitializeLocalPlayerUI(int pid)
    {
        SetLocalPlayerId(pid);
    }
    public void SyncPlayerState(int pid, int money, Dictionary<StockType, int> stocks, Dictionary<StockType, int> pendingClose)
    {
        if (_playerPanels.TryGetValue(pid, out var panel))
        {
            panel.UpdateMoney(money);
            panel.UpdateStocks(stocks);
            panel.UpdatePendingClose(pendingClose);
        }
        else
        {
            _pendingPlayerStates[pid] = new PendingPlayerState
            {
                money = money,
                stocks = new Dictionary<StockType, int>(stocks)
            };
        }
    }

    public void ShowWinner(string winnerName, int winnerMoney)
    {
        if (winnerText != null)
        {
            winnerText.text = $"Game is finished. {winnerName} wins with {winnerMoney}$!";
            winnerText.gameObject.SetActive(true);
        }
    }

    public void SyncEndGame(int pid, int money, Dictionary<StockType, int> stocks)
    {
        if (_playerPanels.TryGetValue(pid, out var panel))
        {
            panel.UpdateMoney(money);
            panel.UpdateStocks(stocks);
        }
    }

    public void ShowLocalToast(string msg, float duration = 5f)
    {
        localPrompt.gameObject.SetActive(true);
        localPrompt.text = msg;

        StopCoroutine(nameof(HideToastCo));
        StartCoroutine(HideToastCo(duration));
    }

    private IEnumerator HideToastCo(float delay)
    {
        yield return new WaitForSeconds(delay);
        localPrompt.gameObject.SetActive(false);
    }

    public void ShowGlobalBanner(string msg, float duration = 3f)
    {
        globalPrompt.gameObject.SetActive(true);
        globalPrompt.text = msg;

        StopCoroutine (nameof(HideGlobalCo));
        StartCoroutine(HideGlobalCo(duration));
    }

    private IEnumerator HideGlobalCo(float delay)
    {
        yield return new WaitForSeconds(delay);
        globalPrompt.gameObject.SetActive(false);
    }

    public void HideGlobalBanner()
    {
        globalPrompt.gameObject.SetActive(false);
    }


    private NetPlayer LocalNetPlayer =>
        Mirror.NetworkClient.isConnected ? Mirror.NetworkClient.localPlayer?.GetComponent<NetPlayer>() : null;
}
