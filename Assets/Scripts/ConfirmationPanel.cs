using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class ConfirmationPanel : MonoBehaviour
{
    [SerializeField] private TextMeshProUGUI message;
    [SerializeField] private Button confirmButton;
    [SerializeField] private Button cancelButton;

    private Action _onConfirm, _onCancel;

    public void Show(string msg, Action onConfirm, Action onCancel, bool localOnly = true)
    {
        _onConfirm = onConfirm;
        _onCancel = onCancel;

        if (message) message.text = msg;

        // Gate to local player only (so remote clients can't click)
        var cg = GetComponent<CanvasGroup>();
        if (cg)
        {
            cg.alpha = localOnly ? 1f : 0f;
            cg.interactable = localOnly;
            cg.blocksRaycasts = localOnly;
        }

        gameObject.SetActive(true);

        confirmButton.onClick.RemoveAllListeners();
        cancelButton.onClick.RemoveAllListeners();

        confirmButton.onClick.AddListener(() => { Hide(); _onConfirm?.Invoke(); });
        cancelButton.onClick.AddListener(() => { Hide(); _onCancel?.Invoke();  });        
    }

    public void Hide()
    {
        gameObject.SetActive(false);
        _onConfirm = _onCancel = null;
    }
}
