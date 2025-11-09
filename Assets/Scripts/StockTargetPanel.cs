using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class StockTargetPanel : MonoBehaviour
{
    [SerializeField] private Button redBtn, blueBtn, greenBtn, yellowBtn;
    //[SerializeField] private TextMeshProUGUI prompt;
    [SerializeField] private Button cancelBtn;

    private Action<StockType> _onChosen;
    private Action _onCancel;

    public void Show(int actingPid, HashSet<StockType> enabled, string promptText, Action<StockType> onChosen, Action onCancelled)
    {
        gameObject.SetActive(true);
        _onChosen = onChosen;
        _onCancel = onCancelled;

        //if (prompt != null)
        //{
        //    if (string.IsNullOrEmpty(promptText))
        //        prompt.gameObject.SetActive(false);
        //    else
        //    {
        //        prompt.text = promptText;
        //        prompt.gameObject.SetActive(true);
        //    }
        //}

        //prompt.gameObject.SetActive(true);
        //if (prompt) prompt.text = promptText;
        //if (prompt) prompt.text = $"{PlayerManager.Instance.players[actingPid].playerName}, choose a stock to manipulate";

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
