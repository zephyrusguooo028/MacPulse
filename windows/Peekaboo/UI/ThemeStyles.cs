using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Win32;

namespace Peekaboo;

/// Implicit styles for the stock WPF controls (text boxes, combo boxes, check boxes,
/// scroll bars, tooltips, context menus) so they follow the light / dark theme instead
/// of always rendering with the light Aero2 look. Colours come from DynamicResources
/// that are updated when Windows switches theme.
public static class ThemeStyles
{
    const string Xaml = """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

  <Style TargetType="TextBox">
    <Setter Property="Foreground" Value="{DynamicResource PkText}"/>
    <Setter Property="Background" Value="{DynamicResource PkInput}"/>
    <Setter Property="BorderBrush" Value="{DynamicResource PkLine}"/>
    <Setter Property="CaretBrush" Value="{DynamicResource PkText}"/>
    <Setter Property="SelectionBrush" Value="{DynamicResource PkAccent}"/>
    <Setter Property="Padding" Value="6,4"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="TextBox">
          <Border x:Name="Bd" CornerRadius="6" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1">
            <ScrollViewer x:Name="PART_ContentHost" Margin="{TemplateBinding Padding}" VerticalAlignment="Center"/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsKeyboardFocused" Value="True">
              <Setter TargetName="Bd" Property="BorderBrush" Value="{DynamicResource PkAccent}"/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType="ComboBoxItem">
    <Setter Property="Foreground" Value="{DynamicResource PkText}"/>
    <Setter Property="Padding" Value="8,5"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="ComboBoxItem">
          <Border x:Name="Bd" CornerRadius="4" Padding="{TemplateBinding Padding}" Background="Transparent">
            <ContentPresenter/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsHighlighted" Value="True">
              <Setter TargetName="Bd" Property="Background" Value="{DynamicResource PkHover}"/>
            </Trigger>
            <Trigger Property="IsSelected" Value="True">
              <Setter TargetName="Bd" Property="Background" Value="{DynamicResource PkAccentSoft}"/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType="ComboBox">
    <Setter Property="Foreground" Value="{DynamicResource PkText}"/>
    <Setter Property="Background" Value="{DynamicResource PkInput}"/>
    <Setter Property="BorderBrush" Value="{DynamicResource PkLine}"/>
    <Setter Property="MinHeight" Value="28"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="ComboBox">
          <Grid>
            <ToggleButton x:Name="Toggle" Focusable="False" ClickMode="Press"
                          IsChecked="{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}">
              <ToggleButton.Template>
                <ControlTemplate TargetType="ToggleButton">
                  <Border x:Name="Bd" CornerRadius="6" BorderThickness="1"
                          Background="{Binding Background, RelativeSource={RelativeSource AncestorType=ComboBox}}"
                          BorderBrush="{Binding BorderBrush, RelativeSource={RelativeSource AncestorType=ComboBox}}">
                    <Path HorizontalAlignment="Right" VerticalAlignment="Center" Margin="0,0,10,0" Data="M0,0 L4,4 L8,0"
                          Stroke="{DynamicResource PkSecondary}" StrokeThickness="1.4"/>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True">
                      <Setter TargetName="Bd" Property="BorderBrush" Value="{DynamicResource PkAccent}"/>
                    </Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </ToggleButton.Template>
            </ToggleButton>
            <ContentPresenter IsHitTestVisible="False" Margin="10,4,28,4" VerticalAlignment="Center"
                              Content="{TemplateBinding SelectionBoxItem}" ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}"/>
            <Popup IsOpen="{TemplateBinding IsDropDownOpen}" Placement="Bottom" AllowsTransparency="True" Focusable="False" PopupAnimation="Fade">
              <Border Background="{DynamicResource PkCard}" BorderBrush="{DynamicResource PkLine}" BorderThickness="1" CornerRadius="8" Padding="4"
                      MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}" MaxHeight="320">
                <ScrollViewer><ItemsPresenter/></ScrollViewer>
              </Border>
            </Popup>
          </Grid>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType="CheckBox">
    <Setter Property="Foreground" Value="{DynamicResource PkText}"/>
    <Setter Property="Cursor" Value="Hand"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="CheckBox">
          <StackPanel Orientation="Horizontal" Background="Transparent">
            <Border x:Name="Box" Width="16" Height="16" CornerRadius="4" BorderThickness="1.2" VerticalAlignment="Center"
                    Background="{DynamicResource PkInput}" BorderBrush="{DynamicResource PkSecondary}">
              <Path x:Name="Mark" Data="M2.5,7.5 L6,11 L12.5,3.5" Stroke="White" StrokeThickness="1.8" Visibility="Collapsed"
                    StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"/>
            </Border>
            <ContentPresenter x:Name="Content" Margin="7,0,0,0" VerticalAlignment="Center"/>
          </StackPanel>
          <ControlTemplate.Triggers>
            <Trigger Property="IsChecked" Value="True">
              <Setter TargetName="Box" Property="Background" Value="{DynamicResource PkAccent}"/>
              <Setter TargetName="Box" Property="BorderBrush" Value="{DynamicResource PkAccent}"/>
              <Setter TargetName="Mark" Property="Visibility" Value="Visible"/>
            </Trigger>
            <Trigger Property="Content" Value="{x:Null}">
              <Setter TargetName="Content" Property="Margin" Value="0"/>
            </Trigger>
            <Trigger Property="IsEnabled" Value="False">
              <Setter Property="Opacity" Value="0.45"/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType="ScrollBar">
    <Setter Property="Background" Value="Transparent"/>
    <Setter Property="Width" Value="10"/>
    <Setter Property="MinWidth" Value="10"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="ScrollBar">
          <Track x:Name="PART_Track" IsDirectionReversed="True">
            <Track.Thumb>
              <Thumb>
                <Thumb.Template>
                  <ControlTemplate TargetType="Thumb">
                    <Border CornerRadius="4" Margin="2" Background="{DynamicResource PkScroll}"/>
                  </ControlTemplate>
                </Thumb.Template>
              </Thumb>
            </Track.Thumb>
          </Track>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
    <Style.Triggers>
      <Trigger Property="Orientation" Value="Horizontal">
        <Setter Property="Width" Value="Auto"/>
        <Setter Property="MinWidth" Value="0"/>
        <Setter Property="Height" Value="10"/>
        <Setter Property="Template">
          <Setter.Value>
            <ControlTemplate TargetType="ScrollBar">
              <Track x:Name="PART_Track" IsDirectionReversed="False">
                <Track.Thumb>
                  <Thumb>
                    <Thumb.Template>
                      <ControlTemplate TargetType="Thumb">
                        <Border CornerRadius="4" Margin="2" Background="{DynamicResource PkScroll}"/>
                      </ControlTemplate>
                    </Thumb.Template>
                  </Thumb>
                </Track.Thumb>
              </Track>
            </ControlTemplate>
          </Setter.Value>
        </Setter>
      </Trigger>
    </Style.Triggers>
  </Style>

  <Style TargetType="ToolTip">
    <Setter Property="Foreground" Value="{DynamicResource PkText}"/>
    <Setter Property="Background" Value="{DynamicResource PkCard}"/>
    <Setter Property="BorderBrush" Value="{DynamicResource PkLine}"/>
    <Setter Property="Padding" Value="8,4"/>
  </Style>

  <Style TargetType="ContextMenu">
    <Setter Property="Foreground" Value="{DynamicResource PkText}"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="ContextMenu">
          <Border Background="{DynamicResource PkCard}" BorderBrush="{DynamicResource PkLine}" BorderThickness="1" CornerRadius="8" Padding="4">
            <StackPanel IsItemsHost="True"/>
          </Border>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType="MenuItem">
    <Setter Property="Foreground" Value="{DynamicResource PkText}"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="MenuItem">
          <Border x:Name="Bd" CornerRadius="4" Padding="10,6" Background="Transparent">
            <ContentPresenter ContentSource="Header"/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsHighlighted" Value="True">
              <Setter TargetName="Bd" Property="Background" Value="{DynamicResource PkHover}"/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType="Separator">
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="Separator">
          <Border Height="1" Margin="6,4" Background="{DynamicResource PkLine}"/>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
</ResourceDictionary>
""";

    public static void Install(Application app)
    {
        app.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(Xaml));
        Apply(app);
        // Follow Windows when the user flips between light and dark.
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
                app.Dispatcher.BeginInvoke(() =>
                {
                    bool wasDark = Theme.Dark;
                    Theme.Load();
                    Apply(app);
                    if (wasDark != Theme.Dark) ThemeChanged?.Invoke();
                });
        };
    }

    public static event Action? ThemeChanged;

    /// Pushes the current Theme colours into the resources the styles use.
    public static void Apply(Application app)
    {
        var r = app.Resources;
        r["PkText"] = Theme.Text;
        r["PkSecondary"] = Theme.Secondary;
        r["PkCard"] = Theme.Card;
        r["PkLine"] = Theme.Line;
        r["PkHover"] = Theme.Hover;
        r["PkAccent"] = Theme.AccentBrush;
        r["PkAccentSoft"] = Theme.AccentSoftBrush;
        r["PkInput"] = Theme.B(Theme.Dark ? Color.FromRgb(36, 36, 36) : Colors.White);
        r["PkScroll"] = Theme.B(Theme.Dark ? Color.FromArgb(90, 255, 255, 255) : Color.FromArgb(70, 0, 0, 0));
    }
}
