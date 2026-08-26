using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;

namespace codeloomapp;

public partial class MainWindow
{
    private bool _visualPolishInitialized;

    private void InitializeVisualPolish()
    {
        if (_visualPolishInitialized)
            return;

        _visualPolishInitialized = true;
        InstallDarkControlStyles();
    }

    private void InstallDarkControlStyles()
    {
        var resources = (ResourceDictionary)XamlReader.Parse(
            """
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <Style x:Key="CodeLoomTabItem" TargetType="TabItem">
                    <Setter Property="Foreground" Value="#D9DEE4" />
                    <Setter Property="Background" Value="#13171B" />
                    <Setter Property="BorderBrush" Value="#20262D" />
                    <Setter Property="Padding" Value="13,7" />
                    <Setter Property="FontSize" Value="12" />
                    <Setter Property="Cursor" Value="Hand" />
                    <Setter Property="Template">
                        <Setter.Value>
                            <ControlTemplate TargetType="TabItem">
                                <Border x:Name="TabBorder"
                                        Background="{TemplateBinding Background}"
                                        BorderBrush="{TemplateBinding BorderBrush}"
                                        BorderThickness="0,0,1,1"
                                        Padding="{TemplateBinding Padding}">
                                    <ContentPresenter ContentSource="Header"
                                                      HorizontalAlignment="Center"
                                                      VerticalAlignment="Center"
                                                      RecognizesAccessKey="True"
                                                      TextElement.Foreground="{TemplateBinding Foreground}" />
                                </Border>
                                <ControlTemplate.Triggers>
                                    <Trigger Property="IsMouseOver" Value="True">
                                        <Setter TargetName="TabBorder" Property="Background" Value="#181E24" />
                                        <Setter TargetName="TabBorder" Property="BorderBrush" Value="#29323A" />
                                        <Setter Property="Foreground" Value="#E7EBEF" />
                                    </Trigger>
                                    <Trigger Property="IsSelected" Value="True">
                                        <Setter TargetName="TabBorder" Property="Background" Value="#1D242B" />
                                        <Setter TargetName="TabBorder" Property="BorderBrush" Value="#34404A" />
                                        <Setter Property="Foreground" Value="#F2F5F7" />
                                    </Trigger>
                                    <Trigger Property="IsEnabled" Value="False">
                                        <Setter Property="Opacity" Value="0.45" />
                                    </Trigger>
                                </ControlTemplate.Triggers>
                            </ControlTemplate>
                        </Setter.Value>
                    </Setter>
                </Style>

                <Style x:Key="CodeLoomExpander" TargetType="Expander">
                    <Setter Property="Foreground" Value="#89939D" />
                    <Setter Property="FontSize" Value="11" />
                    <Setter Property="Template">
                        <Setter.Value>
                            <ControlTemplate TargetType="Expander">
                                <DockPanel>
                                    <ToggleButton x:Name="HeaderSite"
                                                  DockPanel.Dock="Top"
                                                  IsChecked="{Binding IsExpanded, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}"
                                                  Content="{TemplateBinding Header}"
                                                  ContentTemplate="{TemplateBinding HeaderTemplate}"
                                                  Foreground="{TemplateBinding Foreground}"
                                                  Background="Transparent"
                                                  BorderThickness="0"
                                                  Padding="0"
                                                  HorizontalContentAlignment="Stretch"
                                                  Cursor="Hand">
                                        <ToggleButton.Template>
                                            <ControlTemplate TargetType="ToggleButton">
                                                <Grid Background="Transparent">
                                                    <Grid.ColumnDefinitions>
                                                        <ColumnDefinition Width="15" />
                                                        <ColumnDefinition Width="*" />
                                                    </Grid.ColumnDefinitions>
                                                    <TextBlock x:Name="Chevron"
                                                               Text="›"
                                                               FontFamily="Segoe UI Symbol"
                                                               FontSize="15"
                                                               Foreground="#68737E"
                                                               HorizontalAlignment="Left"
                                                               VerticalAlignment="Center"
                                                               Margin="1,-1,0,0" />
                                                    <ContentPresenter Grid.Column="1"
                                                                      Content="{TemplateBinding Content}"
                                                                      ContentTemplate="{TemplateBinding ContentTemplate}"
                                                                      HorizontalAlignment="Stretch"
                                                                      VerticalAlignment="Center"
                                                                      TextElement.Foreground="{TemplateBinding Foreground}" />
                                                </Grid>
                                                <ControlTemplate.Triggers>
                                                    <Trigger Property="IsMouseOver" Value="True">
                                                        <Setter TargetName="Chevron" Property="Foreground" Value="#A0AAB4" />
                                                        <Setter Property="Foreground" Value="#AAB3BC" />
                                                    </Trigger>
                                                    <Trigger Property="IsChecked" Value="True">
                                                        <Setter TargetName="Chevron" Property="Text" Value="⌄" />
                                                        <Setter TargetName="Chevron" Property="Foreground" Value="#8E99A4" />
                                                    </Trigger>
                                                </ControlTemplate.Triggers>
                                            </ControlTemplate>
                                        </ToggleButton.Template>
                                    </ToggleButton>
                                    <ContentPresenter x:Name="ExpandSite"
                                                      Visibility="Collapsed"
                                                      Focusable="False"
                                                      Content="{TemplateBinding Content}"
                                                      ContentTemplate="{TemplateBinding ContentTemplate}" />
                                </DockPanel>
                                <ControlTemplate.Triggers>
                                    <Trigger Property="IsExpanded" Value="True">
                                        <Setter TargetName="ExpandSite" Property="Visibility" Value="Visible" />
                                    </Trigger>
                                </ControlTemplate.Triggers>
                            </ControlTemplate>
                        </Setter.Value>
                    </Setter>
                </Style>

                <Style x:Key="CodeLoomDataGridHeader" TargetType="DataGridColumnHeader">
                    <Setter Property="Foreground" Value="#AEB7C0" />
                    <Setter Property="Background" Value="#151A1F" />
                    <Setter Property="BorderBrush" Value="#242B32" />
                    <Setter Property="BorderThickness" Value="0,0,1,1" />
                    <Setter Property="Padding" Value="6,5" />
                    <Setter Property="FontWeight" Value="SemiBold" />
                    <Setter Property="FontSize" Value="10" />
                    <Setter Property="HorizontalContentAlignment" Value="Left" />
                    <Setter Property="Template">
                        <Setter.Value>
                            <ControlTemplate TargetType="DataGridColumnHeader">
                                <Border x:Name="HeaderBorder"
                                        Background="{TemplateBinding Background}"
                                        BorderBrush="{TemplateBinding BorderBrush}"
                                        BorderThickness="{TemplateBinding BorderThickness}"
                                        Padding="{TemplateBinding Padding}">
                                    <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"
                                                      VerticalAlignment="Center"
                                                      TextElement.Foreground="{TemplateBinding Foreground}" />
                                </Border>
                                <ControlTemplate.Triggers>
                                    <Trigger Property="IsMouseOver" Value="True">
                                        <Setter TargetName="HeaderBorder" Property="Background" Value="#1A2026" />
                                    </Trigger>
                                </ControlTemplate.Triggers>
                            </ControlTemplate>
                        </Setter.Value>
                    </Setter>
                </Style>
            </ResourceDictionary>
            """);

        var tabStyle = (Style)resources["CodeLoomTabItem"];
        var expanderStyle = (Style)resources["CodeLoomExpander"];
        var headerStyle = (Style)resources["CodeLoomDataGridHeader"];

        Resources[typeof(TabItem)] = tabStyle;
        Resources[typeof(Expander)] = expanderStyle;
        Resources[typeof(DataGridColumnHeader)] = headerStyle;

        foreach (var tab in MainTabs.Items.OfType<TabItem>())
            tab.Style = tabStyle;

        foreach (var expander in FindVisualChildren<Expander>(this))
            expander.Style = expanderStyle;

        VariablesGrid.ColumnHeaderStyle = headerStyle;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
                yield return match;

            foreach (var descendant in FindVisualChildren<T>(child))
                yield return descendant;
        }
    }
}
