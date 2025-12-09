using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class StockTargetPanel : MonoBehaviour
{
    [SerializeField] private Button redBtn, blueBtn, greenBtn, yellowBtn;
    [SerializeField] private Button cancelBtn;

    private Action<StockType> _onChosen;
    private Action _onCancel;

    public void Show(int actingPid, HashSet<StockType> enabled, string promptText, Action<StockType> onChosen, Action onCancelled)
    {
        gameObject.SetActive(true);
        _onChosen = onChosen;
        _onCancel = onCancelled;

        Setup(redBtn, StockType.Red, enabled.Contains(StockType.Red));
        Setup(blueBtn, StockType.Blue, enabled.Contains(StockType.Blue));
        Setup(greenBtn, StockType.Green, enabled.Contains(StockType.Green));
        if (yellowBtn) Setup(yellowBtn, StockType.Yellow, enabled.Contains(StockType.Yellow));

        cancelBtn.onClick.RemoveAllListeners();
        cancelBtn.onClick.AddListener(() => { gameObject.SetActive(false); _onCancel?.Invoke(); });
    }

    private void Setup(Button btn, StockType stock, bool interactable)
    {
        if (!btn) return;
        btn.interactable = interactable;
        btn.onClick.RemoveAllListeners();
        btn.onClick.AddListener(() => { gameObject.SetActive(false); _onChosen?.Invoke(stock); });
        btn.gameObject.SetActive(true);
    }
}
