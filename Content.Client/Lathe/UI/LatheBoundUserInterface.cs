using Content.Shared._Ganimed.Components; // Ganimed edit
using Content.Shared.ADT.Salvage; // ADT
using Content.Shared.Lathe;
using Content.Shared.Research.Components;
using JetBrains.Annotations;
using Robust.Client.GameObjects;
using Robust.Client.UserInterface;
using Robust.Shared.IoC;

namespace Content.Client.Lathe.UI
{
    [UsedImplicitly]
    public sealed class LatheBoundUserInterface : BoundUserInterface
    {
        [ViewVariables]
        private LatheMenu? _menu;

        private readonly IEntityManager _entityManager; // Ganimed edit

        public LatheBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
        {
            _entityManager = IoCManager.Resolve<IEntityManager>(); // Ganimed edit
        }

        protected override void Open()
        {
            base.Open();

            _menu = this.CreateWindowCenteredRight<LatheMenu>();
            _menu.SetEntity(Owner);

            _menu.OnServerListButtonPressed += _ =>
            {
                SendMessage(new ConsoleServerSelectionMessage());
            };

            _menu.RecipeQueueAction += (recipe, amount) =>
            {
                SendMessage(new LatheQueueRecipeMessage(recipe, amount));
            };

            _menu.OnClaimMiningPoints += () => SendMessage(new LatheClaimMiningPointsMessage()); // ADT
        }

        protected override void UpdateState(BoundUserInterfaceState state)
        {
            base.UpdateState(state);

            switch (state)
            {
                case LatheUpdateState msg:
                    if (_menu != null)
                    {
                        _menu.Recipes = msg.Recipes;
                        // Ganimed edit start
                        _menu.CurrentAlertLevel = msg.CurrentAlertLevel;

                        // Обновляем компонент ограничения по уровню угрозы
                        if (_entityManager.TryGetComponent<LatheAlertLevelRestrictionComponent>(Owner, out var restrictionComp))
                        {
                            restrictionComp.CurrentAlertLevel = msg.CurrentAlertLevel;
                        }
                    }
                        // Ganimed edit end
                    _menu?.PopulateRecipes();
                    _menu?.UpdateCategories();
                    _menu?.PopulateQueueList(msg.Queue);
                    _menu?.SetQueueInfo(msg.CurrentlyProducing);
                    break;
            }
        }
    }
}
