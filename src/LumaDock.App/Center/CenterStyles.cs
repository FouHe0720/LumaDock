using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using ComboBox = System.Windows.Controls.ComboBox;
using CheckBox = System.Windows.Controls.CheckBox;

namespace LumaDock.App.Center;

/// <summary>Small native templates with no frame loop or external visual dependency.</summary>
internal static class CenterStyles
{
    private static readonly Lazy<Style> DesktopMenu = new(() => (Style)XamlReader.Parse("""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ContextMenu">
          <Style.Resources>
            <Style TargetType="Separator">
              <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Separator"><Border Height="0.7" Margin="9,5" Background="{DynamicResource BorderBrush}"/></ControlTemplate></Setter.Value></Setter>
            </Style>
          </Style.Resources>
          <Setter Property="Background" Value="{DynamicResource PopupBrush}"/>
          <Setter Property="Foreground" Value="{DynamicResource TextBrush}"/>
          <Setter Property="BorderBrush" Value="{DynamicResource GlassHighlightBrush}"/>
          <Setter Property="FontFamily" Value="{DynamicResource UiTextFamily}"/>
          <Setter Property="FontWeight" Value="{DynamicResource UiTextWeight}"/>
          <Setter Property="FontStyle" Value="{DynamicResource UiTextStyle}"/>
          <Setter Property="FontSize" Value="13"/>
          <Setter Property="Padding" Value="5"/>
          <Setter Property="SnapsToDevicePixels" Value="True"/>
          <Setter Property="KeyboardNavigation.DirectionalNavigation" Value="Cycle"/>
          <Setter Property="Template"><Setter.Value>
            <ControlTemplate TargetType="ContextMenu">
              <Border x:Name="DesktopMenuSurface" CornerRadius="12" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="0.8" Padding="{TemplateBinding Padding}">
                <ScrollViewer MaxHeight="520" HorizontalScrollBarVisibility="Disabled" VerticalScrollBarVisibility="Auto" CanContentScroll="True">
                  <ItemsPresenter x:Name="ItemsPresenter" KeyboardNavigation.DirectionalNavigation="Cycle" KeyboardNavigation.TabNavigation="Cycle"/>
                </ScrollViewer>
              </Border>
            </ControlTemplate>
          </Setter.Value></Setter>
        </Style>
        """));

    private static readonly Lazy<Style> DesktopMenuItem = new(() => (Style)XamlReader.Parse("""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="MenuItem">
          <Setter Property="Foreground" Value="{DynamicResource TextBrush}"/>
          <Setter Property="Background" Value="Transparent"/>
          <Setter Property="FontFamily" Value="{DynamicResource UiTextFamily}"/>
          <Setter Property="FontWeight" Value="{DynamicResource UiTextWeight}"/>
          <Setter Property="FontStyle" Value="{DynamicResource UiTextStyle}"/>
          <Setter Property="FontSize" Value="13"/>
          <Setter Property="Padding" Value="11,9"/>
          <Setter Property="MinWidth" Value="196"/>
          <Setter Property="Margin" Value="1"/>
          <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
          <Setter Property="SnapsToDevicePixels" Value="True"/>
          <Setter Property="Template"><Setter.Value>
            <ControlTemplate TargetType="MenuItem">
              <Grid>
                <Border x:Name="ItemFace" CornerRadius="8" Background="{TemplateBinding Background}" BorderThickness="0.7" BorderBrush="Transparent" Padding="{TemplateBinding Padding}">
                  <Grid>
                    <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/><ColumnDefinition Width="16"/></Grid.ColumnDefinitions>
                    <ContentPresenter x:Name="HeaderHost" ContentSource="Header" RecognizesAccessKey="True" VerticalAlignment="Center" TextElement.Foreground="{TemplateBinding Foreground}"/>
                    <TextBlock Grid.Column="1" Text="{TemplateBinding InputGestureText}" Foreground="{DynamicResource MutedBrush}" Margin="12,0,2,0" VerticalAlignment="Center"/>
                    <Path x:Name="SubmenuArrow" Grid.Column="2" Data="M 0 0 L 4 4 L 0 8" Stroke="{TemplateBinding Foreground}" StrokeThickness="1.4" HorizontalAlignment="Right" VerticalAlignment="Center" Visibility="Collapsed"/>
                  </Grid>
                </Border>
                <Popup x:Name="PART_Popup" Placement="Right" HorizontalOffset="2" IsOpen="{Binding IsSubmenuOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}" AllowsTransparency="True" Focusable="False" PopupAnimation="None">
                  <Border x:Name="SubmenuSurface" Background="{DynamicResource PopupBrush}" BorderBrush="{DynamicResource GlassHighlightBrush}" BorderThickness="0.8" CornerRadius="12" Padding="5" MinWidth="204" Margin="0,0,4,4">
                    <ScrollViewer MaxHeight="440" HorizontalScrollBarVisibility="Disabled" VerticalScrollBarVisibility="Auto" CanContentScroll="True">
                      <ItemsPresenter x:Name="ItemsPresenter" KeyboardNavigation.DirectionalNavigation="Cycle" KeyboardNavigation.TabNavigation="Cycle"/>
                    </ScrollViewer>
                  </Border>
                </Popup>
              </Grid>
              <ControlTemplate.Triggers>
                <Trigger Property="HasItems" Value="True"><Setter TargetName="SubmenuArrow" Property="Visibility" Value="Visible"/></Trigger>
                <Trigger Property="IsHighlighted" Value="True"><Setter TargetName="ItemFace" Property="Background" Value="{DynamicResource SelectionBrush}"/><Setter TargetName="ItemFace" Property="BorderBrush" Value="{DynamicResource GlassHighlightBrush}"/></Trigger>
                <Trigger Property="IsSubmenuOpen" Value="True"><Setter TargetName="ItemFace" Property="Background" Value="{DynamicResource SelectionBrush}"/></Trigger>
                <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="ItemFace" Property="Background" Value="{DynamicResource SelectionBrush}"/><Setter TargetName="ItemFace" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/></Trigger>
                <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.42"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
          </Setter.Value></Setter>
        </Style>
        """));

    internal static Style DesktopMenuStyle() => DesktopMenu.Value;
    internal static Style DesktopMenuItemStyle() => DesktopMenuItem.Value;

    internal static Style ScrollBarStyle() => (Style)XamlReader.Parse("""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ScrollBar">
          <Setter Property="Width" Value="9"/>
          <Setter Property="MinWidth" Value="9"/>
          <Setter Property="Background" Value="Transparent"/>
          <Setter Property="Focusable" Value="False"/>
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="ScrollBar">
                <Grid Background="Transparent" SnapsToDevicePixels="True">
                  <Track x:Name="PART_Track" Minimum="{TemplateBinding Minimum}" Maximum="{TemplateBinding Maximum}" Value="{TemplateBinding Value}" ViewportSize="{TemplateBinding ViewportSize}" Orientation="{TemplateBinding Orientation}" IsDirectionReversed="True">
                    <Track.DecreaseRepeatButton>
                      <RepeatButton x:Name="Decrease" Command="{x:Static ScrollBar.PageUpCommand}" Focusable="False">
                        <RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Border Background="Transparent"/></ControlTemplate></RepeatButton.Template>
                      </RepeatButton>
                    </Track.DecreaseRepeatButton>
                    <Track.Thumb>
                      <Thumb x:Name="Grip" MinHeight="28" Margin="2,3,2,3" Focusable="False">
                        <Thumb.Template>
                          <ControlTemplate TargetType="Thumb">
                            <Border x:Name="Face" Background="{DynamicResource CaptionBrush}" CornerRadius="2.5" Opacity="0.46"/>
                            <ControlTemplate.Triggers>
                              <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Face" Property="Opacity" Value="0.8"/></Trigger>
                              <Trigger Property="IsDragging" Value="True"><Setter TargetName="Face" Property="Opacity" Value="0.95"/></Trigger>
                              <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Face" Property="Opacity" Value="0.2"/></Trigger>
                            </ControlTemplate.Triggers>
                          </ControlTemplate>
                        </Thumb.Template>
                      </Thumb>
                    </Track.Thumb>
                    <Track.IncreaseRepeatButton>
                      <RepeatButton x:Name="Increase" Command="{x:Static ScrollBar.PageDownCommand}" Focusable="False">
                        <RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Border Background="Transparent"/></ControlTemplate></RepeatButton.Template>
                      </RepeatButton>
                    </Track.IncreaseRepeatButton>
                  </Track>
                </Grid>
                <ControlTemplate.Triggers>
                  <Trigger Property="Orientation" Value="Horizontal">
                    <Setter Property="Width" Value="Auto"/>
                    <Setter Property="MinWidth" Value="0"/>
                    <Setter Property="Height" Value="9"/>
                    <Setter Property="MinHeight" Value="9"/>
                    <Setter TargetName="PART_Track" Property="IsDirectionReversed" Value="False"/>
                    <Setter TargetName="Grip" Property="MinHeight" Value="0"/>
                    <Setter TargetName="Grip" Property="MinWidth" Value="28"/>
                    <Setter TargetName="Grip" Property="Margin" Value="3,2,3,2"/>
                    <Setter TargetName="Decrease" Property="Command" Value="{x:Static ScrollBar.PageLeftCommand}"/>
                    <Setter TargetName="Increase" Property="Command" Value="{x:Static ScrollBar.PageRightCommand}"/>
                  </Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>
        """);

    internal static readonly ControlTemplate SliderTemplate = (ControlTemplate)XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Slider">
          <Grid Height="24" Background="Transparent" SnapsToDevicePixels="True">
            <Track x:Name="PART_Track" Orientation="Horizontal" Minimum="{TemplateBinding Minimum}" Maximum="{TemplateBinding Maximum}" Value="{Binding Value, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}" IsDirectionReversed="{TemplateBinding IsDirectionReversed}">
              <Track.DecreaseRepeatButton>
                <RepeatButton Focusable="False" Command="{x:Static Slider.DecreaseLarge}" Background="{DynamicResource AccentBrush}">
                  <RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Grid Height="24" Background="Transparent"><Border Height="3" VerticalAlignment="Center" CornerRadius="1.5" Background="{TemplateBinding Background}"/></Grid></ControlTemplate></RepeatButton.Template>
                </RepeatButton>
              </Track.DecreaseRepeatButton>
              <Track.Thumb>
                <Thumb x:Name="Thumb" Width="15" Height="15" Focusable="False" Cursor="Hand">
                  <Thumb.Template><ControlTemplate TargetType="Thumb"><Border x:Name="Handle" CornerRadius="5,8,5,8" Background="{DynamicResource TextBrush}" BorderBrush="{DynamicResource GlassHighlightBrush}" BorderThickness="1"/><ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Handle" Property="Background" Value="{DynamicResource AccentBrush}"/></Trigger><Trigger Property="IsDragging" Value="True"><Setter TargetName="Handle" Property="Background" Value="{DynamicResource AccentBrush}"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Thumb.Template>
                </Thumb>
              </Track.Thumb>
              <Track.IncreaseRepeatButton>
                <RepeatButton Focusable="False" Command="{x:Static Slider.IncreaseLarge}" Background="{DynamicResource BorderBrush}">
                  <RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Grid Height="24" Background="Transparent"><Border Height="3" VerticalAlignment="Center" CornerRadius="1.5" Background="{TemplateBinding Background}"/></Grid></ControlTemplate></RepeatButton.Template>
                </RepeatButton>
              </Track.IncreaseRepeatButton>
            </Track>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.4"/></Trigger>
            <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Thumb" Property="Opacity" Value="0.7"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
        """);

    internal static readonly ControlTemplate ToggleTemplate = (ControlTemplate)XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="CheckBox">
          <Grid Width="44" Height="26" Background="Transparent" Cursor="Hand">
            <Border x:Name="Track" CornerRadius="8,13,13,8" Background="{DynamicResource ToggleTrackBrush}"/>
            <Border x:Name="Knob" Width="20" Height="20" Margin="3" CornerRadius="6,10,10,6" Background="White" HorizontalAlignment="Left"/>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property="IsChecked" Value="True">
              <Setter TargetName="Track" Property="Background" Value="{DynamicResource AccentBrush}"/>
              <Setter TargetName="Knob" Property="HorizontalAlignment" Value="Right"/>
            </Trigger>
            <Trigger Property="IsMouseOver" Value="True"><Setter Property="Opacity" Value="0.8"/></Trigger>
            <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Track" Property="BorderThickness" Value="2"/><Setter TargetName="Track" Property="BorderBrush" Value="{DynamicResource TextBrush}"/></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.4"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
        """);

    internal static Style ComboStyle() => (Style)XamlReader.Parse("""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ComboBox">
          <Setter Property="Foreground" Value="{DynamicResource TextBrush}"/>
          <Setter Property="Background" Value="{DynamicResource PanelBrush}"/>
          <Setter Property="BorderBrush" Value="{DynamicResource GlassHighlightBrush}"/>
          <Setter Property="BorderThickness" Value="1"/>
          <Setter Property="FontSize" Value="12"/>
          <Setter Property="FontFamily" Value="{DynamicResource UiTextFamily}"/>
          <Setter Property="FontWeight" Value="{DynamicResource UiTextWeight}"/>
          <Setter Property="FontStyle" Value="{DynamicResource UiTextStyle}"/>
          <Setter Property="ItemContainerStyle">
            <Setter.Value>
              <Style TargetType="ComboBoxItem">
                <Setter Property="Foreground" Value="{DynamicResource TextBrush}"/>
                <Setter Property="FontFamily" Value="{DynamicResource UiTextFamily}"/>
                <Setter Property="FontWeight" Value="{DynamicResource UiTextWeight}"/>
                <Setter Property="FontStyle" Value="{DynamicResource UiTextStyle}"/>
                <Setter Property="Padding" Value="12,9"/>
                <Setter Property="Template">
                  <Setter.Value><ControlTemplate TargetType="ComboBoxItem">
                    <Border x:Name="Item" CornerRadius="6" Background="Transparent" Padding="{TemplateBinding Padding}" Margin="3,1"><ContentPresenter/></Border>
                    <ControlTemplate.Triggers>
                      <Trigger Property="IsHighlighted" Value="True"><Setter TargetName="Item" Property="Background" Value="{DynamicResource BorderBrush}"/></Trigger>
                      <Trigger Property="IsSelected" Value="True"><Setter Property="FontWeight" Value="{DynamicResource UiEmphasisWeight}"/></Trigger>
                    </ControlTemplate.Triggers>
                  </ControlTemplate></Setter.Value>
                </Setter>
              </Style>
            </Setter.Value>
          </Setter>
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="ComboBox">
                <Grid>
                  <ToggleButton Focusable="False" IsChecked="{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}" ClickMode="Press" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1">
                    <ToggleButton.Template>
                      <ControlTemplate TargetType="ToggleButton"><Border x:Name="Button" Background="{TemplateBinding Background}" CornerRadius="9,14,9,14" BorderThickness="0.8" BorderBrush="{TemplateBinding BorderBrush}"><Grid><TextBlock Text="⌄" Foreground="{DynamicResource MutedBrush}" HorizontalAlignment="Right" VerticalAlignment="Center" Margin="0,0,12,3"/></Grid></Border><ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Button" Property="Opacity" Value="0.75"/></Trigger></ControlTemplate.Triggers></ControlTemplate>
                    </ToggleButton.Template>
                  </ToggleButton>
                  <ContentPresenter Margin="12,8,30,8" IsHitTestVisible="False" VerticalAlignment="Center" Content="{TemplateBinding SelectionBoxItem}" ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}" ContentTemplateSelector="{TemplateBinding ItemTemplateSelector}"/>
                  <Popup x:Name="PART_Popup" Placement="Bottom" IsOpen="{TemplateBinding IsDropDownOpen}" AllowsTransparency="True" Focusable="False" PopupAnimation="None">
                    <Border Background="{DynamicResource PopupBrush}" BorderBrush="{DynamicResource BorderBrush}" BorderThickness="1" CornerRadius="12" MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}" Padding="4" Margin="0,4,0,0">
                      <ScrollViewer MaxHeight="270" CanContentScroll="True"><ItemsPresenter KeyboardNavigation.DirectionalNavigation="Contained"/></ScrollViewer>
                    </Border>
                  </Popup>
                </Grid>
                <ControlTemplate.Triggers><Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.4"/></Trigger></ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>
        """);
}
