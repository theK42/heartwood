using System;
using System.Collections.Generic;
using AYellowpaper.SerializedCollections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Heartwood
{
    public class View : MonoBehaviour
    {
        [SerializeField] private SerializedDictionary<string, GameObject> references;

        public GameObject GetReference(string referenceName)
        {
            if (!references.TryGetValue(referenceName, out var go))
                throw new KeyNotFoundException(
                    $"View on '{name}' has no reference named '{referenceName}'.");
            return go;
        }

        public void SetText(string referenceName, string text)
        {
            var go = GetReference(referenceName);
            if (go == null)
            {
                throw new KeyNotFoundException($"View on '{name}' has no reference '{referenceName}'.");
            }
            
            var tmpText = go.GetComponent<TMP_Text>();
            if (tmpText != null)
            {
                tmpText.text = text;
            }
            else
            {
                var uiText = go.GetComponent<Text>();
                if (uiText != null)
                {
                    uiText.text = text;
                }
                else
                {
                    throw new MissingComponentException($"View on '{name}' has no text component on reference '{referenceName}'.");
                }
                
            }
        }

        public void SetAction(string referenceName, Action action)
        {
            var go = GetReference(referenceName);
            if (go == null)
            {
                throw new KeyNotFoundException($"View on '{name}' has no reference '{referenceName}'.");
            }
            var button = go.GetComponent<Button>();
            if (button == null)
            {
                throw new MissingComponentException($"View on '{name}' has no button component on reference '{referenceName}'.");
            }
            button.onClick.AddListener(() => action());
        }
    }
}
