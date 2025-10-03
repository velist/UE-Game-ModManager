using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows.Input;

namespace UEModManager.ViewModels
{
    // 移除了所有认证相关的ViewModels
    public class DummyCommand : ICommand
    {
        public bool CanExecute(object? parameter) => true;
        
        public void Execute(object? parameter) { }
        
        public event EventHandler? CanExecuteChanged;
        
        public async System.Threading.Tasks.Task ExecuteAsync(object? parameter)
        {
            await System.Threading.Tasks.Task.Delay(1);
        }
    }
}