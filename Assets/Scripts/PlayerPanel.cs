using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;
using TMPro;

public class PlayerPanel : MonoBehaviour
{
    [HideInInspector] public int playerId;    // assigned at runtime

    [SerializeField] private Button redOpenSellButton;
    [SerializeField] private Button redCloseSellButton;
    [SerializeField] private Button greenOpenSellButton;
    [SerializeField] private Button greenCloseSellButton;
    [SerializeField] private Button blueOpenSellButton;
    [SerializeField] private Button blueCloseSellButton;
    [SerializeField] private Button yellowOpenSellButton;
    [SerializeField] private Button yellowCloseSellButton;

    [SerializeField] private Button undoButton;

    [SerializeField] private TextMeshProUGUI nameText;
    [SerializeField] private TextMeshProUGUI moneyText;
    
    [SerializeField] private TextMeshProUGUI redStockText;
    [SerializeField] private TextMeshProUGUI blueStockText;
    [SerializeField] private TextMeshProUGUI greenStockText;
    [SerializeField] private TextMeshProUGUI yellowStockText;

    [SerializeField] private TextMeshProUGUI closeSellRedStockText;
    [SerializeField] private TextMeshProUGUI closeSellBlueStockText;
    [SerializeField] private TextMeshProUGUI closeSellGreenStockText;
    [SerializeField] private TextMeshProUGUI closeSellYellowStockText;

    [SerializeField] private GameObject stocksContainer;

    [SerializeField] private Button abilityButton;
    [SerializeField] private Button endTurnButton;

    [SerializeField] private Image activeGlow; // optional overlay in prefab

    public void Initialize(int id, string playerName, bool isLocal)
    {
        playerId = id;
        nameText.text = playerName;

        // Only show stocks for the local player
        stocksContainer.SetActive(isLocal);

        // Undo button only exists / is visible on the local panel
        SetUndoVisible(isLocal);
    
        if (isLocal && undoButton)
        {
            undoButton.onClick.RemoveAllListeners();
            undoButton.onClick.AddListener(() => UIManager.Instance.OnUndoButton());
        }

        SetupSellButtons(isLocal);
        SetupAbilityButton(isLocal);
        SetupEndTurnButton(isLocal);

        // Default: disabled until it's this player's turn
        SetSellButtonsInteractable(false);
        SetAbilityButtonInteractable(false);
        SetEndTurnButtonInteractable(false);
    }

    public void UpdateMoney(int amount)
    {
        moneyText.text = $"Money: {amount}$";
    }

    private void SetupSellButtons(bool isLocal)
    {
        // Only local player gets sell buttons visible
        void Show(Button b)
        {
            if (b)
            {
                b.gameObject.SetActive(isLocal);
            }  
        }
        Show(redOpenSellButton); 
        Show(redCloseSellButton);
        Show(greenOpenSellButton); 
        Show(greenCloseSellButton);
        Show(blueOpenSellButton); 
        Show(blueCloseSellButton);
        Show(yellowOpenSellButton); 
        Show(yellowCloseSellButton);

        if (!isLocal) return;

        // Wire up callbacks to the central UIManager → TurnManager
        void Bind(Button btn, StockType type, bool open)
        {
            if (!btn) return;
            btn.onClick.RemoveAllListeners();
            btn.onClick.AddListener(() =>
            {
                if (playerId != TurnManager.Instance.ActivePlayerId)
                {
                    UIManager.Instance.ShowMessage("Not your turn.");
                    return;
                }
                bool ok = TurnManager.Instance.TrySellOne(type, open);
                if (!ok) UIManager.Instance.ShowMessage("Cannot sell now.");
            });
        }

        Bind(redOpenSellButton, StockType.Red, true);
        Bind(redCloseSellButton, StockType.Red, false);
        Bind(greenOpenSellButton, StockType.Green, true);
        Bind(greenCloseSellButton, StockType.Green, false);
        Bind(blueOpenSellButton, StockType.Blue, true);
        Bind(blueCloseSellButton, StockType.Blue, false);
        Bind(yellowOpenSellButton, StockType.Yellow, true);
        Bind(yellowCloseSellButton, StockType.Yellow, false);
    }

    private void SetupAbilityButton(bool isLocal)
    {
        if (!abilityButton) return;
        abilityButton.gameObject.SetActive(isLocal);
        abilityButton.onClick.RemoveAllListeners();

        if (!isLocal) return;

        abilityButton.onClick.AddListener(() =>
        {
            if (playerId != TurnManager.Instance.ActivePlayerId)
            {
                UIManager.Instance.ShowMessage("Not your turn.");
                return;
            }
            bool ok = TurnManager.Instance.TryUseAbility();
            if (!ok) UIManager.Instance.ShowMessage("Ability not available.");
        });
    }

    private void SetupEndTurnButton(bool isLocal)
    {
        if (!endTurnButton) return;
        endTurnButton.gameObject.SetActive(isLocal);
        endTurnButton.onClick.RemoveAllListeners();

        if (!isLocal) return;

        endTurnButton.onClick.AddListener(() =>
        {
            if (playerId != TurnManager.Instance.ActivePlayerId)
            {
                UIManager.Instance.ShowMessage("Not your turn.");
                return;
            }
            TurnManager.Instance.EndActivePlayerTurn();
        });
    }

    public void SetSellButtonsInteractable(bool interactable)
    {
        // enable/disable only the local player's sell buttons
        redOpenSellButton.interactable = interactable;
        redCloseSellButton.interactable = interactable;
        greenOpenSellButton.interactable = interactable;
        greenCloseSellButton.interactable = interactable;
        blueOpenSellButton.interactable = interactable;
        blueCloseSellButton.interactable = interactable;
        yellowOpenSellButton.interactable = interactable;
        yellowCloseSellButton.interactable = interactable;
    }

    public void SetAbilityButtonInteractable(bool interactable)
    {
        if (abilityButton) abilityButton.interactable = interactable;
    }

    public void SetEndTurnButtonInteractable(bool interactable)
    {
        if (endTurnButton) endTurnButton.interactable = interactable;
    }

    public void SetActiveHighlight(bool on)
    {
        if (activeGlow != null)
        {
            activeGlow.enabled = on;
        }
    }

    public void SetUndoInteractable(bool on)
    {
        if (undoButton) undoButton.interactable = on;
    }

    public void SetUndoVisible(bool on)
    {
        if (undoButton) undoButton.gameObject.SetActive(on);
    }

    public void UpdateStocks(Dictionary<StockType, int> stocks)
    {
        redStockText.text = stocks.TryGetValue(StockType.Red, out var r) ? r.ToString() : "0";
        greenStockText.text = stocks.TryGetValue(StockType.Green, out var g) ? g.ToString() : "0";
        yellowStockText.text = stocks.TryGetValue(StockType.Yellow, out var y) ? y.ToString() : "0";
        blueStockText.text = stocks.TryGetValue(StockType.Blue, out var b) ? b.ToString() : "0";
    }

    public void UpdatePendingClose(StockType stock, int count)
    {
        TextMeshProUGUI t = stock switch
        {
            StockType.Red => closeSellRedStockText,
            StockType.Blue => closeSellBlueStockText,
            StockType.Green => closeSellGreenStockText,
            StockType.Yellow => closeSellYellowStockText,
            _ => null
        };
        if (t == null) return;
        if (count <= 0) t.gameObject.SetActive(false);
        else { t.gameObject.SetActive(true); t.text = $"({count})"; }
    }

    public void ClearPendingCloseAll()
    {
        UpdatePendingClose(StockType.Red, 0);
        UpdatePendingClose(StockType.Blue, 0);
        UpdatePendingClose(StockType.Green, 0);
        UpdatePendingClose(StockType.Yellow, 0);
    }
}
