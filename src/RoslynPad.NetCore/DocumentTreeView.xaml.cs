﻿using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using RoslynPad.UI;

namespace RoslynPad
{
    class DocumentTreeView : UserControl
    {
        private MainViewModel _viewModel;

        public DocumentTreeView()
        {
            AvaloniaXamlLoader.Load(this);
            var treeView = this.Find<TreeView>("Tree");
            treeView.ItemContainerGenerator.Materialized += ItemContainerGenerator_Materialized;
            treeView.ItemContainerGenerator.Dematerialized += ItemContainerGenerator_Dematerialized;
        }

        private void ItemContainerGenerator_Materialized(object sender, Avalonia.Controls.Generators.ItemContainerEventArgs e)
        {
            foreach (var item in e.Containers)
            {
                if (item.ContainerControl is TreeViewItem treeViewItem)
                {
                    treeViewItem.PointerPressed += OnDocumentClick;
                    treeViewItem.KeyDown += OnDocumentKeyDown;
                }
            }
        }

        private void ItemContainerGenerator_Dematerialized(object sender, Avalonia.Controls.Generators.ItemContainerEventArgs e)
        {
            foreach (var item in e.Containers)
            {
                if (item.ContainerControl is TreeViewItem treeViewItem)
                {
                    treeViewItem.PointerPressed -= OnDocumentClick;
                    treeViewItem.KeyDown -= OnDocumentKeyDown;
                }
            }
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            _viewModel = DataContext as MainViewModel;
        }


        private void OnDocumentClick(object sender, PointerPressedEventArgs e)
        {
            if (e.MouseButton == MouseButton.Left && e.ClickCount >= 2)
            {
                OpenDocument(e.Source);
            }
        }

        private void OnDocumentKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                OpenDocument(e.Source);
            }
        }

        private void OpenDocument(object source)
        {
            var documentViewModel = (DocumentViewModel)((Control)source).DataContext;
            _viewModel.OpenDocument(documentViewModel);
        }
    }
}
